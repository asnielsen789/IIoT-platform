using Azure.Messaging.EventHubs;
using IIoT.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions;

/// <summary>
/// The IoT Hub entry point. Kept deliberately thin: it extracts the authenticated identity from
/// the transport and delegates every decision to <see cref="TelemetryService"/>, which has no
/// Azure dependency and is therefore testable without one.
/// </summary>
public sealed class TelemetryFunction
{
    /// <summary>
    /// IoT Hub stamps the authenticated device identity onto every message under this system
    /// property. A device cannot set it — that is what makes it authoritative, and what makes
    /// the <c>deviceId</c> in the body merely informational. See section 4 of the contract.
    /// </summary>
    private const string ConnectionDeviceIdProperty = "iothub-connection-device-id";

    private readonly TelemetryService _telemetryService;
    private readonly ILogger<TelemetryFunction> _logger;

    public TelemetryFunction(TelemetryService telemetryService, ILogger<TelemetryFunction> logger)
    {
        _telemetryService = telemetryService;
        _logger = logger;
    }

    [Function(nameof(ProcessTelemetry))]
    public async Task ProcessTelemetry(
        [EventHubTrigger("messages/events", Connection = "IoTHubConnection", IsBatched = false)]
        EventData eventData,
        FunctionContext context)
    {
        var authenticatedDeviceId = ReadAuthenticatedDeviceId(eventData);
        var payload = eventData.EventBody.ToString();

        var outcome = await _telemetryService.ProcessAsync(payload, authenticatedDeviceId, context.CancellationToken);

        // The message is never re-thrown on rejection. A payload that fails validation will fail
        // identically on every retry, so throwing would only block the partition until the
        // message expires. Dead-lettering is tracked separately.
        if (outcome != ProcessingOutcome.Forwarded)
        {
            _logger.LogDebug(
                "Message from {DeviceId} completed with outcome {Outcome}",
                authenticatedDeviceId ?? "(unknown device)",
                outcome);
        }
    }

    private static string? ReadAuthenticatedDeviceId(EventData eventData) =>
        eventData.SystemProperties.TryGetValue(ConnectionDeviceIdProperty, out var value)
            ? value as string
            : null;
}
