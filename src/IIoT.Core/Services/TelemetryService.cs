using IIoT.Core.Interfaces;
using IIoT.Core.Validation;
using Microsoft.Extensions.Logging;

namespace IIoT.Core.Services;

/// <summary>
/// Validates an incoming payload, establishes that it came from the device it claims to come
/// from, and hands it to the forwarder.
/// </summary>
/// <remarks>
/// Deliberately free of any Azure dependency: the transport hands in the payload and the
/// authenticated identity, so the whole decision path is unit-testable without a cloud.
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
    public async Task<ProcessingOutcome> ProcessAsync(
        string payload,
        string? authenticatedDeviceId,
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

        await _forwarder.ForwardAsync(message, cancellationToken);
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
    RejectedUnknownIdentity
}
