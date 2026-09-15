using IIoT.Core.Interfaces;
using IIoT.Core.Models;
using Microsoft.Extensions.Logging;

namespace IIoT.Core.Forwarders;

public sealed class GatewayV1Forwarder : IDataForwarder
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GatewayV1Forwarder> _logger;

    public GatewayV1Forwarder(HttpClient httpClient, ILogger<GatewayV1Forwarder> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task ForwardAsync(SensorReading reading, CancellationToken cancellationToken = default)
    {
        // TODO: Implement downstream gateway integration once the target interface is specified
        _logger.LogInformation("Forwarding reading from {DeviceId} to gateway v1", reading.DeviceId);
        await Task.CompletedTask;
    }
}
