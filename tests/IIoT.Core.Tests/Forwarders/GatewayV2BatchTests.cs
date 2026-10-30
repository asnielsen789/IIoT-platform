using IIoT.Core.Forwarders;
using IIoT.Core.Models;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Validation;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// The mapping onto gateway contract v2 (ADR-011), checked against the published v2 schema: every
/// message the platform accepts becomes a body that schema accepts, with its sensors and its kinds.
/// </summary>
public class GatewayV2BatchTests
{
    private static readonly ForwardingContext Context = new("charger-000123", new DateTimeOffset(2026, 9, 14, 6, 0, 13, 250, TimeSpan.Zero));

    /// <summary>
    /// v2 carries every type, so every valid device example maps, those v1 carries included. If the
    /// device contract gains something v2 cannot carry, this is where it shows.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractFixtures.Valid), MemberType = typeof(ContractFixtures))]
    public void EveryValidDeviceMessage_MapsToABodyTheV2SchemaAccepts(string fileName)
    {
        var body = GatewayV2Batch.From(Parse(fileName), Context).ToUtf8Json();

        var errors = GatewayV2Schema.Errors(body);
        Assert.True(errors.Count == 0, $"{fileName} mapped to an invalid v2 body: {string.Join("; ", errors)}");
    }

    [Fact]
    public void Charger_MapsEveryKind_WithIdentityAndTimeFromThePlatform()
    {
        var batch = GatewayV2Batch.From(Parse("valid-ev-charger.json"), Context);

        Assert.Equal(2, batch.ContractVersion);
        Assert.Equal("charger-000123", batch.DeviceId);
        Assert.Equal(Context.EnqueuedAt, batch.ReceivedAt);
        Assert.Equal(["energy", "power", "count"], batch.Readings.Select(reading => reading.Kind));
        Assert.Equal(["kW.h", "kW", "1"], batch.Readings.Select(reading => reading.Unit));
        Assert.Equal([null, null, "sessions"], batch.Readings.Select(reading => reading.Sensor));
    }

    /// <summary>
    /// The wire: a reading without a sensor has no <c>sensor</c> at all, and a count is written
    /// without a fraction, as the schema's integer requires.
    /// </summary>
    [Fact]
    public void Json_LeavesOutAbsentSensors_AndWritesCountsAsIntegers()
    {
        var json = System.Text.Encoding.UTF8.GetString(GatewayV2Batch.From(Parse("valid-ev-charger.json"), Context).ToUtf8Json());

        Assert.Contains("\"value\":1873,", json, StringComparison.Ordinal);
        Assert.Contains("\"sensor\":\"sessions\"", json, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"sensor\""));
    }

    [Fact]
    public void AnUnknownType_HasNoV2Mapping()
    {
        var message = Parse("valid-minimal.json") with
        {
            Measurements = [new Measurement { Type = "temperature", Value = 4, Unit = "Cel", MeasuredAt = Context.EnqueuedAt }]
        };

        var exception = Assert.Throws<ForwardingException>(() => GatewayV2Batch.From(message, Context));
        Assert.Contains("has no mapping in gateway contract v2", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The device schema ties each type to its unit; the mapping checks the pair again rather than trusting it.</summary>
    [Fact]
    public void ATypeInAnotherTypesUnit_HasNoV2Mapping()
    {
        var message = Parse("valid-minimal.json") with
        {
            Measurements = [new Measurement { Type = "energy", Value = 4, Unit = "mm", MeasuredAt = Context.EnqueuedAt }]
        };

        Assert.Throws<ForwardingException>(() => GatewayV2Batch.From(message, Context));
    }

    private static TelemetryMessage Parse(string fileName) =>
        TelemetryValidator.Validate(ContractFixtures.Read(fileName)).Message!;
}
