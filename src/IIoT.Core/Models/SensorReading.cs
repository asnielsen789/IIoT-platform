namespace IIoT.Core.Models;

public sealed record SensorReading
{
    public required string DeviceId { get; init; }
    public required double Level { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Unit { get; init; }
}
