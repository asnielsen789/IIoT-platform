using Azure.Messaging.EventHubs;
using IIoT.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions;

/// <summary>
/// The IoT Hub entry point. Kept deliberately thin: it extracts the authenticated identity and
/// the enqueued time from the transport and delegates every decision to
/// <see cref="TelemetryService"/>, which has no Azure dependency and is therefore testable
/// without one.
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

    /// <remarks>
    /// The consumer group comes from the <c>IoTHubConsumerGroup</c> app setting: a group of the
    /// Function's own, so it does not compete for partitions with other readers of
    /// <c>$Default</c>, such as <c>az iot hub monitor-events</c>. The setting has no default, and
    /// the trigger cannot start without it, locally included. See infra/README.md.
    /// </remarks>
    [Function(nameof(ProcessTelemetry))]
    public async Task ProcessTelemetry(
        [EventHubTrigger(
            "messages/events",
            Connection = "IoTHubConnection",
            ConsumerGroup = "%IoTHubConsumerGroup%",
            IsBatched = false)]
        EventData eventData,
        FunctionContext context)
    {
        var authenticatedDeviceId = ReadAuthenticatedDeviceId(eventData);
        var payload = eventData.EventBody.ToString();

        // Stamped by IoT Hub when it accepted the message, so it is platform time rather than
        // device time. Downstream it becomes the receipt time; the device's own sentAt and
        // measuredAt are not trustworthy as absolute time (section 4 of the contract).
        var enqueuedAt = eventData.EnqueuedTime;

        var outcome = await _telemetryService.ProcessAsync(
            payload, authenticatedDeviceId, enqueuedAt, context.CancellationToken);

        // The message is never re-thrown on rejection. A payload that fails validation will fail
        // identically on every retry, so throwing would only block the partition until the
        // message expires. A forwarding failure is not re-thrown either: the forwarder has
        // already retried, and TelemetryService has logged it. Until dead-lettering exists
        // (#19), a message that failed to forward is lost.
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
