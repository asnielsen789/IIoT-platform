namespace IIoT.Core.Models;

public sealed record DeviceConfig
{
    public required string DeviceId { get; init; }
    public required int ReportingIntervalSeconds { get; init; }
    public double? ThresholdHigh { get; init; }
    public double? ThresholdLow { get; init; }
}
