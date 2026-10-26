using Azure.Messaging.EventHubs;
using IIoT.Core.Services;
using IIoT.Functions.DeadLettering;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions;

/// <summary>
/// The IoT Hub entry point. Kept deliberately thin: it extracts the authenticated identity and
/// the enqueued time from the transport and delegates every decision to
/// <see cref="TelemetryService"/>, which has no Azure dependency and is therefore testable
/// without one. A message whose forwarding failed is handed to the
/// <see cref="ForwardingDeadLetterWriter"/>, for <see cref="RetryForwardingFunction"/> to try again
/// later (ADR-010).
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
    private readonly ForwardingDeadLetterWriter _deadLetters;
    private readonly ILogger<TelemetryFunction> _logger;

    public TelemetryFunction(
        TelemetryService telemetryService,
        ForwardingDeadLetterWriter deadLetters,
        ILogger<TelemetryFunction> logger)
    {
        _telemetryService = telemetryService;
        _deadLetters = deadLetters;
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

        var result = await _telemetryService.ProcessAsync(
            payload, authenticatedDeviceId, enqueuedAt, context.CancellationToken);

        // Never rethrown, whatever the outcome. A payload that fails validation will fail
        // identically on every retry, and the Event Hubs trigger advances its checkpoint past a
        // failed invocation anyway, so throwing would bring nothing back.
        //
        // A forwarding failure is dead-lettered instead: the forwarder has already retried, and the
        // retry function takes it from here. Only a forwarding failure is: a rejection is final, and
        // TelemetryService has logged it, an identity mismatch as a security event. The identity is
        // the authenticated one, since forwarding is attempted only after the identity check.
        if (result.Outcome == ProcessingOutcome.ForwardingFailed)
        {
            await _deadLetters.DeadLetterAsync(
                payload, authenticatedDeviceId!, enqueuedAt, result, context.CancellationToken);
            return;
        }

        if (result.Outcome != ProcessingOutcome.Forwarded)
        {
            _logger.LogDebug(
                "Message from {DeviceId} completed with outcome {Outcome}",
                authenticatedDeviceId ?? "(unknown device)",
                result.Outcome);
        }
    }

    private static string? ReadAuthenticatedDeviceId(EventData eventData) =>
        eventData.SystemProperties.TryGetValue(ConnectionDeviceIdProperty, out var value)
            ? value as string
            : null;
}
