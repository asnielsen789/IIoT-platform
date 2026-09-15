using System.Text.Json;
using IIoT.Core.Models;
using IIoT.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions;

public sealed class SensorDataFunction
{
    private readonly SensorDataService _sensorDataService;
    private readonly ILogger<SensorDataFunction> _logger;

    public SensorDataFunction(SensorDataService sensorDataService, ILogger<SensorDataFunction> logger)
    {
        _sensorDataService = sensorDataService;
        _logger = logger;
    }

    [Function(nameof(ProcessSensorData))]
    public async Task ProcessSensorData(
        [EventHubTrigger("messages/events", Connection = "IoTHubConnection", IsBatched = false)] string message,
        FunctionContext context)
    {
        var reading = JsonSerializer.Deserialize<SensorReading>(message, JsonOptions.Default);
        if (reading is null)
        {
            _logger.LogWarning("Failed to deserialize sensor reading");
            return;
        }

        await _sensorDataService.ProcessReadingAsync(reading, context.CancellationToken);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
