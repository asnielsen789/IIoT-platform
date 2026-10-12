using IIoT.Core.Interfaces;
using IIoT.Core.Models;
using IIoT.Core.Validation;
using Microsoft.Extensions.Logging;

namespace IIoT.Core.Services;

/// <summary>
/// Validates an incoming payload, establishes that it came from the device it claims to come
/// from, and hands it to the forwarder.
/// </summary>
/// <remarks>
/// Deliberately free of any Azure dependency: the transport hands in the payload, the
/// authenticated identity and the enqueued time, so the whole decision path is unit-testable
/// without a cloud.
/// </remarks>
public sealed class TelemetryService
{
    private readonly IDataForwarder _forwarder;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(IDataForwarder forwarder, ILogger<TelemetryService> logger)
    {
        _forwarder = forwarder;
        _logger = logger;
    }

    /// <param name="payload">The raw message body.</param>
    /// <param name="authenticatedDeviceId">
    /// The value of the IoT Hub system property <c>iothub-connection-device-id</c>. IoT Hub
    /// derives it from the device's certificate and a device cannot set it, which is what makes
    /// it the authoritative identity. <c>null</c> means it was absent, and the message is
    /// rejected rather than processed under an unknown identity.
    /// </param>
    /// <param name="enqueuedAt">
    /// When IoT Hub accepted the message. Passed through to the forwarder as the receipt time,
    /// because unlike the device's own timestamps it comes from the platform's clock.
    /// </param>
    public async Task<ProcessingOutcome> ProcessAsync(
        string payload,
        string? authenticatedDeviceId,
        DateTimeOffset enqueuedAt,
        CancellationToken cancellationToken = default)
    {
        var result = TelemetryValidator.Validate(payload);
        if (!result.IsValid)
        {
            _logger.LogWarning(
                "Rejected message from {DeviceId}: {Reason}",
                authenticatedDeviceId ?? "(unknown device)",
                result.Error);

            return ProcessingOutcome.RejectedInvalidPayload;
        }

        var message = result.Message!;

        if (string.IsNullOrWhiteSpace(authenticatedDeviceId))
        {
            // Either the trigger is not bound to an IoT Hub message, or routing dropped the
            // system properties. Both are configuration faults, and neither is a reason to
            // accept data whose origin cannot be established.
            _logger.LogError(
                "Security event: message {MessageId} carries no authenticated device identity; rejected",
                message.MessageId);

            return ProcessingOutcome.RejectedUnknownIdentity;
        }

        // Contract section 4. The body's deviceId is informational and may be absent; when it
        // is present it must agree with the authenticated identity. Without this check a device
        // holding a valid certificate could file readings against another installation.
        if (message.DeviceId is not null &&
            !string.Equals(message.DeviceId, authenticatedDeviceId, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Security event: message {MessageId} from authenticated device {AuthenticatedDeviceId} " +
                "claims deviceId {ClaimedDeviceId}; rejected",
                message.MessageId,
                authenticatedDeviceId,
                message.DeviceId);

            return ProcessingOutcome.RejectedIdentityMismatch;
        }

        // Several measurements mean buffered backfill after an outage. Every entry is processed,
        // and an old measuredAt is never a reason to reject one — that is the normal shape of
        // recovered data, and the most valuable kind to keep.
        foreach (var measurement in message.Measurements)
        {
            _logger.LogInformation(
                "Device {DeviceId} measured {Type}={Value} {Unit} at {MeasuredAt} (quality {Quality})",
                authenticatedDeviceId,
                measurement.Type,
                measurement.Value,
                measurement.Unit,
                measurement.MeasuredAt,
                measurement.Quality);
        }

        // From here on the identity is the authenticated one. The context is built only after
        // the cross-check above, so a forwarder can never be handed an identity that was not
        // proven, and it never needs to look at the payload's deviceId at all.
        var context = new ForwardingContext(authenticatedDeviceId, enqueuedAt);

        try
        {
            await _forwarder.ForwardAsync(message, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // By now the forwarder has already retried whatever was worth retrying, so this
            // failure is final for this attempt: the gateway refused the message, stayed
            // unavailable through every retry, or could not be reached or authenticated to.
            //
            // THE MESSAGE IS CURRENTLY LOST. Nothing downstream has it, and nothing here keeps
            // it. Durable dead-lettering, so a failed message can be inspected and replayed, is
            // #19 and does not exist yet. Until then this log line is the only record.
            //
            // Not rethrown, consistent with the rejections above. Rethrowing would not bring the
            // message back: the trigger checkpoints past a failed invocation unless a retry
            // policy is configured, and with one, a gateway outage would hold the whole
            // partition behind this single message.
            //
            // Cancellation by the caller is not a forwarding failure; it propagates so the host
            // can shut down cleanly.
            _logger.LogError(
                ex,
                "Forwarding failed for message {MessageId} from device {DeviceId}: {Reason}. " +
                "The message was not delivered and will not be retried",
                message.MessageId,
                authenticatedDeviceId,
                ex.Message);

            return ProcessingOutcome.ForwardingFailed;
        }

        return ProcessingOutcome.Forwarded;
    }
}

/// <summary>
/// What happened to a message. Returned rather than only logged so that tests can assert on the
/// decision, and so the counts can be published as metrics when observability is built out.
/// </summary>
public enum ProcessingOutcome
{
    Forwarded,
    RejectedInvalidPayload,
    RejectedIdentityMismatch,
    RejectedUnknownIdentity,

    /// <summary>
    /// Valid and authenticated, but not delivered downstream. The message is currently lost;
    /// dead-lettering is #19.
    /// </summary>
    ForwardingFailed
}
