using IIoT.Core.Forwarders;
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
    /// <returns>
    /// The outcome, with the message id and, for a forwarding failure, its reason: what a caller
    /// that dead-letters the message needs (ADR-010). The service itself never keeps a message.
    /// </returns>
    public async Task<ProcessingResult> ProcessAsync(
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

            return ProcessingResult.Rejected(ProcessingOutcome.RejectedInvalidPayload);
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

            return ProcessingResult.Rejected(ProcessingOutcome.RejectedUnknownIdentity, message.MessageId);
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

            return ProcessingResult.Rejected(ProcessingOutcome.RejectedIdentityMismatch, message.MessageId);
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
            // Not rethrown, consistent with the rejections above. What happens to the message next
            // is the caller's decision, not this service's: the IoT Hub function dead-letters it,
            // and the retry function leaves it on its queue for another attempt (ADR-010). Both
            // need the reason, so it travels in the result as well as in this line.
            //
            // Cancellation by the caller is not a forwarding failure; it propagates so the host
            // can shut down cleanly.
            _logger.LogError(
                ex,
                "Forwarding failed for message {MessageId} from device {DeviceId}: {Reason}",
                message.MessageId,
                authenticatedDeviceId,
                ex.Message);

            return ProcessingResult.ForwardingFailed(
                message.MessageId,
                ex.Message,
                ex is ForwardingException { StatusCode: { } status } ? (int)status : null);
        }

        return ProcessingResult.Forwarded(message.MessageId);
    }
}

/// <summary>
/// What happened to a message. Returned rather than only logged so that tests can assert on the
/// decision, and so the counts can be published as metrics when observability is built out.
/// </summary>
/// <remarks>
/// The three rejections are permanent: the same payload under the same identity is rejected the
/// same way every time, so retrying one is pointless. Only <see cref="ForwardingFailed"/> can turn
/// out differently on another attempt, and only it is dead-lettered (ADR-010).
/// </remarks>
public enum ProcessingOutcome
{
    Forwarded,
    RejectedInvalidPayload,
    RejectedIdentityMismatch,
    RejectedUnknownIdentity,

    /// <summary>
    /// Valid and authenticated, but not delivered downstream after the forwarder's own retries.
    /// The IoT Hub function dead-letters the message for a later attempt (ADR-010).
    /// </summary>
    ForwardingFailed
}
