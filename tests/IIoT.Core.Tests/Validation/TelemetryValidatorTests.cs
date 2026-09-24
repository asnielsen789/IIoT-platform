using IIoT.Core.Models;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Validation;

namespace IIoT.Core.Tests.Validation;

public class TelemetryValidatorTests
{
    /// <summary>
    /// Guards the two theories below. A theory whose data source silently returns nothing would
    /// otherwise look like a suite that passes, which is the failure mode worth catching: it
    /// happens whenever the fixtures stop being copied to the output directory.
    /// </summary>
    [Fact]
    public void Fixtures_AreDiscovered()
    {
        Assert.True(ContractFixtures.Valid().Count() >= 4, "valid fixtures were not copied to the output directory");
        Assert.True(ContractFixtures.Invalid().Count() >= 4, "invalid fixtures were not copied to the output directory");
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.Valid), MemberType = typeof(ContractFixtures))]
    public void Validate_ValidFixture_IsAccepted(string fileName)
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read(fileName));

        Assert.True(result.IsValid, $"{fileName} should be valid but was rejected: {result.Error}");
        Assert.NotNull(result.Message);
        Assert.Null(result.Error);
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.Invalid), MemberType = typeof(ContractFixtures))]
    public void Validate_InvalidFixture_IsRejected(string fileName)
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read(fileName));

        Assert.False(result.IsValid, $"{fileName} should have been rejected but was accepted");
        Assert.Null(result.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        // The reason has to name what was wrong. Without this the test would still pass if the
        // validator fell back to its generic message, which is useless in a log.
        Assert.DoesNotContain("does not match the v1 schema", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_FullMessage_DeserialisesEveryField()
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read("valid-full.json"));

        Assert.NotNull(result.Message);
        var message = result.Message!;

        Assert.Equal(1, message.SchemaVersion);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", message.MessageId);
        Assert.Equal("tank-000123", message.DeviceId);
        Assert.Equal(4711, message.Sequence);

        var measurement = Assert.Single(message.Measurements);
        Assert.Equal("level", measurement.Type);
        Assert.Equal(1432.5, measurement.Value);
        Assert.Equal("mm", measurement.Unit);
        Assert.Equal(MeasurementQuality.Ok, measurement.Quality);

        Assert.Equal("1.4.2", message.Device.Firmware);

        Assert.NotNull(message.Device.Battery);
        Assert.Equal(3.61, message.Device.Battery!.Value);
        Assert.Equal("V", message.Device.Battery.Unit);

        Assert.NotNull(message.Device.Signal);
        Assert.Equal(-104, message.Device.Signal!.Rsrp);
        Assert.Equal(-11, message.Device.Signal.Rsrq);
    }

    [Fact]
    public void Validate_MinimalMessage_DefaultsQualityToOk()
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read("valid-minimal.json"));

        Assert.NotNull(result.Message);
        var message = result.Message!;

        Assert.Null(message.DeviceId);
        Assert.Null(message.Sequence);
        Assert.Null(message.Device.Battery);
        Assert.Null(message.Device.Signal);
        Assert.Equal(MeasurementQuality.Ok, Assert.Single(message.Measurements).Quality);
    }

    /// <summary>
    /// Buffered backfill. The readings are hours old, which must never be a reason to reject
    /// them, and all three have to survive as separate entries.
    /// </summary>
    [Fact]
    public void Validate_BufferedBackfill_KeepsEveryMeasurement()
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read("valid-buffered-backfill.json"));

        Assert.NotNull(result.Message);
        var message = result.Message!;

        Assert.Equal(3, message.Measurements.Count);
        Assert.Equal(MeasurementQuality.Uncertain, message.Measurements[2].Quality);

        Assert.All(message.Measurements, measurement =>
            Assert.True(measurement.MeasuredAt < message.SentAt, "backfill is older than the transmission"));
    }

    /// <summary>
    /// Forward compatibility, contract section 6: a v1 consumer meeting fields it does not know
    /// must ignore them, because devices stay in the field for years across firmware versions.
    /// </summary>
    [Fact]
    public void Validate_UnknownFields_AreIgnoredNotRejected()
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read("valid-unknown-fields.json"));

        Assert.True(result.IsValid, result.Error);
        Assert.NotNull(result.Message);
        Assert.Equal("1.5.0", result.Message!.Device.Firmware);
    }

    /// <summary>
    /// Contract section 6: an unrecognised version is rejected explicitly, never read as v1.
    /// A v2 message is a breaking change by definition, so guessing at it would corrupt data.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(0)]
    public void Validate_UnknownSchemaVersion_IsRejectedExplicitly(int version)
    {
        var payload = ContractFixtures.Read("valid-minimal.json")
            .Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {version}", StringComparison.Ordinal);

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("schemaVersion", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(version.ToString(), result.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"schemaVersion\": 1, ")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Validate_MalformedPayload_IsRejectedWithoutThrowing(string payload)
    {
        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>
    /// <c>format</c> is an annotation in JSON Schema unless validation is explicitly switched on.
    /// This asserts that it is, since a malformed timestamp would otherwise pass unnoticed.
    /// </summary>
    [Fact]
    public void Validate_MalformedTimestamp_IsRejected()
    {
        var payload = ContractFixtures.Read("valid-minimal.json")
            .Replace("2026-09-14T06:00:10Z", "the fourteenth of September", StringComparison.Ordinal);

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.Contains("date-time", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason is logged, so it has to point at the offending field rather than restate that
    /// something, somewhere, failed.
    /// </summary>
    [Fact]
    public void Validate_Rejection_NamesTheOffendingLocation()
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read("invalid-negative-level.json"));

        Assert.Equal("/measurements/0/value: -12.0 should be at least 0", result.Error);
    }
}
