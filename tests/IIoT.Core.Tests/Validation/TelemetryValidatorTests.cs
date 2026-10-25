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
    /// Consumer obligation 1 holds at every level of the message, not only where the fixture puts
    /// its unknown fields. <c>device.battery</c> was the one object whose schema said otherwise, so
    /// a firmware that added a field there would have had every one of its messages rejected.
    /// </summary>
    [Theory]
    [InlineData("root")]
    [InlineData("measurement")]
    [InlineData("device")]
    [InlineData("battery")]
    [InlineData("signal")]
    public void Validate_UnknownField_IsIgnoredAtEveryLevel(string level)
    {
        var payload = level switch
        {
            "root" => InlinePayload.Telemetry(extraRootMember: "\"bearer\": \"nb-iot\""),
            "measurement" => InlinePayload.Telemetry(extraMeasurementMember: "\"temperature\": 4.5"),
            "device" => InlinePayload.Telemetry(device: """{ "firmware": "1.4.2", "uptime": 86400 }"""),
            "battery" => InlinePayload.Telemetry(device: """{ "firmware": "1.4.2", "battery": { "value": 3.61, "unit": "V", "temperature": 21 } }"""),
            "signal" => InlinePayload.Telemetry(device: """{ "firmware": "1.4.2", "signal": { "rsrp": -104, "sinr": 7 } }"""),
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };

        var result = TelemetryValidator.Validate(payload);

        Assert.True(result.IsValid, $"an unknown field in {level} was rejected: {result.Error}");
    }

    /// <summary>The known battery fields still arrive when an unknown one sits beside them.</summary>
    [Fact]
    public void Validate_UnknownBatteryField_KeepsTheKnownOnes()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(
            device: """{ "firmware": "1.4.2", "battery": { "value": 3.61, "unit": "V", "chemistry": "Li-SOCl2" } }"""));

        var battery = result.Message?.Device.Battery;
        Assert.NotNull(battery);
        Assert.Equal(3.61, battery!.Value);
        Assert.Equal("V", battery.Unit);
    }

    /// <summary>
    /// A repeated member gives the schema and the model two values to choose from, and they chose
    /// differently: the evaluator checked the first, the deserialiser kept the second. Each of these
    /// passed the schema and was then processed with the value the schema never saw.
    /// </summary>
    [Theory]
    [InlineData("value", "\"value\": 1432.5, \"value\": -50000")]
    [InlineData("messageId", "\"messageId\": \"01J8F2K9QS4T7V0X3Z6B8N1M5E\", \"messageId\": \"a-message-id-far-longer-than-the-64-characters-the-schema-allows-for-one\"")]
    [InlineData("firmware", "\"firmware\": \"1.4.2\", \"firmware\": \"\"")]
    public void Validate_RepeatedMember_IsRejected(string name, string members)
    {
        var payload = name switch
        {
            "value" => InlinePayload.Telemetry().Replace("\"value\": 1432.5", members, StringComparison.Ordinal),
            "messageId" => InlinePayload.Telemetry().Replace("\"messageId\": \"01J8F2K9QS4T7V0X3Z6B8N1M5E\"", members, StringComparison.Ordinal),
            _ => InlinePayload.Telemetry(device: $"{{ {members} }}")
        };

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid, $"a repeated '{name}' was accepted");
        Assert.Contains($"Duplicate property '{name}'", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused in an unknown field too. Ignoring unknown fields means ignoring their values, not
    /// accepting a document whose meaning JSON Schema leaves undefined.
    /// </summary>
    [Fact]
    public void Validate_RepeatedUnknownMember_IsRejected()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(extraRootMember: "\"note\": 1, \"note\": 2"));

        Assert.False(result.IsValid);
        Assert.Contains("Duplicate property 'note'", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema is case-sensitive, so <c>Value</c> is an unknown field to it and goes unchecked.
    /// The model used to bind it without regard to case, over the <c>value</c> the schema had just
    /// approved. Now it is ignored like any other unknown field, and the validated value stands.
    /// </summary>
    [Fact]
    public void Validate_MemberDifferingOnlyInCase_IsAnUnknownFieldAndChangesNothing()
    {
        var payload = InlinePayload.Telemetry(
            extraMeasurementMember: "\"Value\": -50000",
            extraRootMember: "\"MessageId\": \"x\", \"DEVICEID\": \"tank-000999\"");

        var result = TelemetryValidator.Validate(payload);

        Assert.True(result.IsValid, result.Error);
        var message = result.Message!;
        Assert.Equal(1432.5, Assert.Single(message.Measurements).Value);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5E", message.MessageId);
        Assert.Null(message.DeviceId);
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
