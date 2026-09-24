using IIoT.Core.Interfaces;
using IIoT.Core.Models;
using Microsoft.Extensions.Logging;

namespace IIoT.Core.Forwarders;

/// <summary>
/// Forwards telemetry to the v1 downstream gateway format.
/// </summary>
/// <remarks>
/// A stub. The target interface is not specified, so there is nothing to send to yet; a gateway
/// mock is tracked separately. The shape of this class is the point: a new downstream target is
/// a new <see cref="IDataForwarder"/>, not a change to the processing pipeline.
/// </remarks>
public sealed class GatewayV1Forwarder : IDataForwarder
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GatewayV1Forwarder> _logger;

    public GatewayV1Forwarder(HttpClient httpClient, ILogger<GatewayV1Forwarder> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task ForwardAsync(TelemetryMessage message, CancellationToken cancellationToken = default)
    {
        // TODO: implement the downstream call once the gateway interface is specified.
        _logger.LogInformation(
            "Forwarding message {MessageId} with {MeasurementCount} measurement(s) to gateway v1",
            message.MessageId,
            message.Measurements.Count);

        await Task.CompletedTask;
    }
}
