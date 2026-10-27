using IIoT.Core.DeadLettering;
using IIoT.Core.Services;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions.DeadLettering;

/// <summary>
/// Keeps a message whose forwarding failed: writes its envelope to
/// <see cref="DeadLetterQueues.ForwardingFailed"/>, where <c>RetryForwarding</c> picks it up later.
/// </summary>
/// <remarks>
/// <para>
/// Used by the IoT Hub function only. The retry function never dead-letters: an entry that fails
/// again stays on its own queue, so a message cannot circle between the two.
/// </para>
/// <para>
/// A message that cannot be written is lost, as every failed message was before dead-lettering
/// existed, and is logged at <see cref="LogLevel.Critical"/> with its id, its device and the
/// forwarding failure, which is then the only record. That happens when the entry is too large for a
/// queue message, or when the write itself fails. The write is not retried here beyond the Storage
/// client's own retries, and the failure is not rethrown: ADR-010 explains why neither would bring the
/// message back.
/// </para>
/// </remarks>
public sealed class ForwardingDeadLetterWriter
{
    private readonly IDeadLetterQueue _queue;
    private readonly ForwardingRetrySchedule _schedule;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ForwardingDeadLetterWriter> _logger;

    public ForwardingDeadLetterWriter(
        IDeadLetterQueue queue,
        ForwardingRetrySchedule schedule,
        TimeProvider timeProvider,
        ILogger<ForwardingDeadLetterWriter> logger)
    {
        _queue = queue;
        _schedule = schedule;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <param name="payload">The message body exactly as IoT Hub delivered it.</param>
    /// <param name="authenticatedDeviceId">The identity the message was forwarded under.</param>
    /// <param name="enqueuedAt">When IoT Hub accepted the message.</param>
    /// <param name="result">The <see cref="ProcessingOutcome.ForwardingFailed"/> result.</param>
    /// <returns>Whether the entry was written.</returns>
    public async Task<bool> DeadLetterAsync(
        string payload,
        string authenticatedDeviceId,
        DateTimeOffset enqueuedAt,
        ProcessingResult result,
        CancellationToken cancellationToken = default)
    {
        if (result.Outcome != ProcessingOutcome.ForwardingFailed || result.MessageId is null)
        {
            throw new ArgumentException(
                "Only a message whose forwarding failed is dead-lettered; a rejected message would fail the same way again",
                nameof(result));
        }

        var reason = result.FailureReason ?? "Forwarding failed";
        var entry = DeadLetterEnvelopeJson.Serialize(DeadLetterEnvelope.Create(
            payload,
            authenticatedDeviceId,
            enqueuedAt,
            result.MessageId,
            reason,
            result.StatusCode,
            _timeProvider.GetUtcNow()));

        var size = DeadLetterEnvelopeJson.ByteCount(entry);
        if (size > DeadLetterEnvelopeJson.MaxEntryBytes)
        {
            // Refused rather than shortened. A truncated payload would fail validation on every
            // retry, and a shortened one that still validated would forward something the device
            // never sent.
            LogLost(result.MessageId, authenticatedDeviceId, reason,
                $"its entry is {size} bytes, above the {DeadLetterEnvelopeJson.MaxEntryBytes}-byte limit of one queue message",
                exception: null);
            return false;
        }

        try
        {
            await _queue.AddAsync(entry, _schedule.RetryInterval, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogLost(result.MessageId, authenticatedDeviceId, reason, $"writing to {DeadLetterQueues.ForwardingFailed} failed: {ex.Message}", ex);
            return false;
        }

        _logger.LogInformation(
            "Dead-lettered message {MessageId} from device {DeviceId} to {Queue} ({Size} bytes); " +
            "the first retry follows in about {RetryInterval}, at most {MaxAttempts} attempts",
            result.MessageId,
            authenticatedDeviceId,
            DeadLetterQueues.ForwardingFailed,
            size,
            _schedule.RetryInterval,
            _schedule.MaxAttempts);

        return true;
    }

    private void LogLost(string messageId, string deviceId, string reason, string problem, Exception? exception) =>
        _logger.LogCritical(
            exception,
            "Message {MessageId} from device {DeviceId} is lost: forwarding failed ({Reason}) and it could not be " +
            "dead-lettered: {Problem}. This log entry is the only record of it",
            messageId,
            deviceId,
            reason,
            problem);
}
