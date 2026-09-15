using IIoT.Core.Models;
using IIoT.Core.Validation;

namespace IIoT.Core.Tests.Validation;

public class PayloadValidatorTests
{
    [Fact]
    public void Validate_ValidReading_ReturnsValid()
    {
        var reading = new SensorReading
        {
            DeviceId = "sensor-001",
            Level = 42.5,
            Timestamp = DateTimeOffset.UtcNow
        };

        var (isValid, error) = PayloadValidator.Validate(reading);

        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Validate_MissingDeviceId_ReturnsInvalid(string? deviceId)
    {
        var reading = new SensorReading
        {
            DeviceId = deviceId!,
            Level = 42.5,
            Timestamp = DateTimeOffset.UtcNow
        };

        var (isValid, error) = PayloadValidator.Validate(reading);

        Assert.False(isValid);
        Assert.Contains("DeviceId", error);
    }

    [Fact]
    public void Validate_DefaultTimestamp_ReturnsInvalid()
    {
        var reading = new SensorReading
        {
            DeviceId = "sensor-001",
            Level = 42.5,
            Timestamp = default
        };

        var (isValid, error) = PayloadValidator.Validate(reading);

        Assert.False(isValid);
        Assert.Contains("Timestamp", error);
    }
}
