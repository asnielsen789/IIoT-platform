using IIoT.Core.Interfaces;
using IIoT.Core.Models;
using IIoT.Core.Validation;
using Microsoft.Extensions.Logging;

namespace IIoT.Core.Services;

public sealed class SensorDataService
{
    private readonly IDataForwarder _forwarder;
    private readonly ILogger<SensorDataService> _logger;

    public SensorDataService(IDataForwarder forwarder, ILogger<SensorDataService> logger)
    {
        _forwarder = forwarder;
        _logger = logger;
    }

    public async Task ProcessReadingAsync(SensorReading reading, CancellationToken cancellationToken = default)
    {
        var (isValid, error) = PayloadValidator.Validate(reading);
        if (!isValid)
        {
            _logger.LogWarning("Invalid reading from {DeviceId}: {Error}", reading.DeviceId, error);
            return;
        }

        _logger.LogInformation("Processing reading from {DeviceId}: level={Level}", reading.DeviceId, reading.Level);
        await _forwarder.ForwardAsync(reading, cancellationToken);
    }
}
