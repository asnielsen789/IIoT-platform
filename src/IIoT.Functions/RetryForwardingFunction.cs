using System.Globalization;
using System.Text.Json;
using IIoT.Core.DeadLettering;
using IIoT.Core.Services;
using IIoT.Functions.DeadLettering;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions;

/// <summary>
/// Forwards a dead-lettered message again (ADR-010): the second half of dead-letter handling, after
/// <see cref="TelemetryFunction"/> has written the entry.
/// </summary>
/// <remarks>
/// <para>
/// A retry is the first attempt over again, not a shortcut around it. The stored payload goes
/// through <see cref="TelemetryService"/> once more: validated, cross-checked against the stored
/// authenticated identity, and forwarded with the stored enqueued time. The forwarder therefore
/// builds the same batch with the same <c>Idempotency-Key</c>, the message id, so a gateway that
/// stored the batch on an earlier attempt whose answer never arrived acknowledges it again rather
/// than storing it twice.
/// </para>
/// <para>Each entry ends in exactly one of these ways:</para>
/// <list type="bullet">
/// <item><b>Delivered.</b> The function returns, and the runtime deletes the entry.</item>
/// <item><b>Forwarding failed, attempts left.</b> The function throws, so the queue trigger keeps the
/// entry, invisible for the retry interval, and counts the attempt. It is never dead-lettered again:
/// an entry only ever moves forward, from this queue to the poison queue.</item>
/// <item><b>Forwarding failed on the last attempt.</b> The entry goes to the poison queue, moved
/// here rather than by the runtime so that it does not expire (see <see cref="ForwardingRetrySchedule"/>).</item>
/// <item><b>Permanently refused:</b> the payload no longer validates, for example after a change to
/// the contract, or the entry itself cannot be read. Retrying would end the same way every time, so
/// the entry goes straight to the poison queue, with the reason logged. It is never dropped.</item>
/// </list>
/// <para>
/// If a move to the poison queue fails, the function throws instead, and the entry stays where it is
/// for the runtime to try again; at the attempt limit the runtime moves it to the poison queue itself.
/// </para>
/// </remarks>
public sealed class RetryForwardingFunction
{
    /// <summary>The queue trigger's metadata property with the number of times the entry was dequeued.</summary>
    internal const string DequeueCountProperty = "DequeueCount";

    private readonly TelemetryService _telemetryService;
    private readonly IDeadLetterQueue _queue;
    private readonly ForwardingRetrySchedule _schedule;
    private readonly ILogger<RetryForwardingFunction> _logger;

    public RetryForwardingFunction(
        TelemetryService telemetryService,
        IDeadLetterQueue queue,
        ForwardingRetrySchedule schedule,
        ILogger<RetryForwardingFunction> logger)
    {
        _telemetryService = telemetryService;
        _queue = queue;
        _schedule = schedule;
        _logger = logger;
    }

    /// <remarks>
    /// The connection is <see cref="DeadLetterQueues.ConnectionName"/>, the settings the dead-letter
    /// writer uses as well. Attempts and their spacing are the queue trigger's, from <c>host.json</c>.
    /// </remarks>
    [Function(nameof(RetryForwarding))]
    public async Task RetryForwarding(
        [QueueTrigger(DeadLetterQueues.ForwardingFailed, Connection = DeadLetterQueues.ConnectionName)] string entry,
        FunctionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var attempt = ReadDequeueCount(context);
        var attemptText = attempt?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)";

        var read = DeadLetterEnvelopeJson.Read(entry);
        if (!read.IsValid)
        {
            // An entry this build cannot read — malformed, or from a build with a later envelope
            // version — would fail identically on every attempt. Unchanged, so that a build that
            // understands it can be given it again.
            await MoveToPoisonAsync(entry, "(unreadable entry)", "(unknown device)", attemptText,
                $"the entry cannot be read: {read.Error}", cancellationToken);
            return;
        }

        var envelope = read.Envelope!;

        _logger.LogInformation(
            "Retrying dead-lettered message {MessageId} from device {DeviceId}: attempt {Attempt} of {MaxAttempts}",
            envelope.MessageId,
            envelope.AuthenticatedDeviceId,
            attemptText,
            _schedule.MaxAttempts);

        var result = await _telemetryService.ProcessAsync(
            envelope.Payload, envelope.AuthenticatedDeviceId, envelope.EnqueuedAt, cancellationToken);

        switch (result.Outcome)
        {
            case ProcessingOutcome.Forwarded:
                _logger.LogInformation(
                    "Delivered dead-lettered message {MessageId} from device {DeviceId} on attempt {Attempt}; " +
                    "IoT Hub received it at {EnqueuedAt}",
                    envelope.MessageId,
                    envelope.AuthenticatedDeviceId,
                    attemptText,
                    envelope.EnqueuedAt);
                return;

            case ProcessingOutcome.ForwardingFailed when attempt >= _schedule.MaxAttempts:
                await MoveToPoisonAsync(entry, envelope.MessageId, envelope.AuthenticatedDeviceId, attemptText,
                    $"forwarding still failed on the last of {_schedule.MaxAttempts} attempts: {result.FailureReason}",
                    cancellationToken);
                return;

            case ProcessingOutcome.ForwardingFailed:
                _logger.LogWarning(
                    "Attempt {Attempt} of {MaxAttempts} for dead-lettered message {MessageId} from device {DeviceId} " +
                    "failed: {Reason}. The queue retries it in about {RetryInterval}",
                    attemptText,
                    _schedule.MaxAttempts,
                    envelope.MessageId,
                    envelope.AuthenticatedDeviceId,
                    result.FailureReason,
                    _schedule.RetryInterval);

                throw new ForwardingRetryException(
                    $"Attempt {attemptText} to forward dead-lettered message {envelope.MessageId} failed: {result.FailureReason}");

            default:
                // Rejected on re-validation: the contract changed since the entry was written, or the
                // entry was altered. TelemetryService has logged the reason, a mismatch as a security
                // event.
                await MoveToPoisonAsync(entry, envelope.MessageId, envelope.AuthenticatedDeviceId, attemptText,
                    $"it was rejected on retry ({result.Outcome}), which another attempt would not change",
                    cancellationToken);
                return;
        }
    }

    private async Task MoveToPoisonAsync(
        string entry,
        string messageId,
        string deviceId,
        string attempt,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _queue.AddToPoisonAsync(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Failing the invocation keeps the entry on its queue, so it is retried rather than lost;
            // at the attempt limit the runtime moves it to the poison queue itself.
            _logger.LogError(
                ex,
                "Could not move dead-lettered message {MessageId} from device {DeviceId} to {PoisonQueue} on attempt {Attempt} " +
                "({Reason}): {Problem}. It stays on {Queue}",
                messageId,
                deviceId,
                DeadLetterQueues.ForwardingFailedPoison,
                attempt,
                reason,
                ex.Message,
                DeadLetterQueues.ForwardingFailed);

            throw new ForwardingRetryException(
                $"Could not move dead-lettered message {messageId} to {DeadLetterQueues.ForwardingFailedPoison}: {ex.Message}", ex);
        }

        _logger.LogError(
            "Moved dead-lettered message {MessageId} from device {DeviceId} to {PoisonQueue} on attempt {Attempt}: {Reason}. " +
            "It is not retried again; an operator decides what happens to it (infra/README.md, Dead-lettered messages)",
            messageId,
            deviceId,
            DeadLetterQueues.ForwardingFailedPoison,
            attempt,
            reason);
    }

    /// <summary>
    /// The entry's dequeue count, which is the number of this attempt. The worker hands trigger
    /// metadata over as text or JSON rather than as a number, so it is parsed from either. Absent or
    /// unreadable, it is unknown, and the attempt limit is then left to the runtime.
    /// </summary>
    internal static long? ReadDequeueCount(FunctionContext context)
    {
        if (!context.BindingContext.BindingData.TryGetValue(DequeueCountProperty, out var value) || value is null)
        {
            return null;
        }

        var text = value switch
        {
            long number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

        return long.TryParse(text?.Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : null;
    }
}
