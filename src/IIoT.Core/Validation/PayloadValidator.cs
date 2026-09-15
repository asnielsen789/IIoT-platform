using IIoT.Core.Models;

namespace IIoT.Core.Validation;

public static class PayloadValidator
{
    public static (bool IsValid, string? Error) Validate(SensorReading reading)
    {
        if (string.IsNullOrWhiteSpace(reading.DeviceId))
            return (false, "DeviceId is required");

        if (reading.Timestamp == default)
            return (false, "Timestamp is required");

        return (true, null);
    }
}
