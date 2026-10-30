using System.Text.Json;
using IIoT.Core.Forwarders;
using IIoT.Core.Models;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Validation;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// The mapping from the device contract onto the gateway contract, and the sending side of the
/// gateway contract test: whatever the forwarder produces must satisfy the published schema.
/// </summary>
public class GatewayV1BatchTests
{
    private const string AuthenticatedDevice = "tank-000123";

    /// <summary>Not equal to any timestamp in the fixtures, so it cannot be matched by accident.</summary>
    private static readonly DateTimeOffset EnqueuedAt = new(2026, 9, 14, 6, 0, 13, 250, TimeSpan.Zero);

    private static readonly ForwardingContext Context = new(AuthenticatedDevice, EnqueuedAt);

    /// <summary>
    /// Guards the theory below and every schema assertion in this class: without the schema in
    /// the output directory they could not run, and without device fixtures the theory would
    /// silently have no cases.
    /// </summary>
    [Fact]
    public void SchemaAndFixtures_AreCopiedToTheOutputDirectory()
    {
        Assert.True(File.Exists(GatewaySchema.FilePath), $"the gateway schema was not copied to '{GatewaySchema.FilePath}'");
        Assert.True(ContractFixtures.ValidForV1().Count() >= 4, "valid device fixtures were not copied to the output directory");
        Assert.True(ContractFixtures.ValidNeedingV2().Count() >= 3, "the fixtures that need v2 were not copied to the output directory");
    }

    /// <summary>
    /// Every message of levels the platform accepts must become a body the gateway accepts. If the
    /// device contract gains something the gateway contract cannot carry, this is where it shows.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractFixtures.ValidForV1), MemberType = typeof(ContractFixtures))]
    public void EveryValidLevelMessage_MapsToABodyTheGatewaySchemaAccepts(string fileName)
    {
        var message = Parse(fileName);

        var body = GatewayV1Batch.From(message, Context).ToUtf8Json();

        var errors = GatewaySchema.Errors(body);
        Assert.True(errors.Count == 0, $"{fileName} mapped to an invalid body: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Contract v1 has one reading kind and no sensor, and adding either is a breaking change for
    /// it (ADR-011). A message with another type, or with readings that name their sensor, is
    /// refused with the reason: never forwarded without its other readings, and never with an oil
    /// separator's oil and sludge levels merged into one series.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractFixtures.ValidNeedingV2), MemberType = typeof(ContractFixtures))]
    public void MessageNeedingV2_HasNoV1Mapping(string fileName)
    {
        var message = Parse(fileName);

        var exception = Assert.Throws<ForwardingException>(() => GatewayV1Batch.From(message, Context));
        Assert.Contains("has no mapping in gateway contract v1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullMessage_MapsEveryField()
    {
        var batch = GatewayV1Batch.From(Parse("valid-full.json"), Context);

        Assert.Equal(1, batch.ContractVersion);
        Assert.Equal(AuthenticatedDevice, batch.DeviceId);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", batch.MessageId);
        Assert.Equal(EnqueuedAt, batch.ReceivedAt);
        Assert.Equal("1.4.2", batch.Firmware);

        var reading = Assert.Single(batch.Readings);
        Assert.Equal("level", reading.Kind);
        Assert.Equal(1432.5, reading.Value);
        Assert.Equal("mm", reading.Unit);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T06:00:10Z"), reading.At);
        Assert.Equal("ok", reading.Quality);
    }

    /// <summary>
    /// The payload's deviceId is informational and untrusted. Even where it disagrees with the
    /// context — which TelemetryService would already have rejected — the batch carries the
    /// authenticated identity, so the mapping cannot be the place where a spoofed id slips in.
    /// </summary>
    [Fact]
    public void DeviceId_ComesFromTheContext_NotThePayload()
    {
        var message = Parse("valid-full.json");
        Assert.Equal("tank-000123", message.DeviceId);

        var batch = GatewayV1Batch.From(message, Context with { AuthenticatedDeviceId = "tank-000999" });

        Assert.Equal("tank-000999", batch.DeviceId);
    }

    [Fact]
    public void DeviceId_IsPresent_WhenThePayloadHasNone()
    {
        var message = Parse("valid-minimal.json");
        Assert.Null(message.DeviceId);

        var batch = GatewayV1Batch.From(message, Context);

        Assert.Equal(AuthenticatedDevice, batch.DeviceId);
    }

    /// <summary>
    /// receivedAt is the hub's enqueued time, normalised to UTC — not the device's sentAt, which
    /// comes from a clock nobody vouches for.
    /// </summary>
    [Fact]
    public void ReceivedAt_IsTheEnqueuedTime_InUtc()
    {
        var enqueuedInLocalTime = new DateTimeOffset(2026, 9, 14, 8, 0, 13, TimeSpan.FromHours(2));
        var message = Parse("valid-full.json");

        var batch = GatewayV1Batch.From(message, Context with { EnqueuedAt = enqueuedInLocalTime });

        Assert.Equal(enqueuedInLocalTime, batch.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, batch.ReceivedAt.Offset);
        Assert.NotEqual(message.SentAt, batch.ReceivedAt);

        using var json = JsonDocument.Parse(batch.ToUtf8Json());
        Assert.Equal("2026-09-14T06:00:13+00:00", json.RootElement.GetProperty("receivedAt").GetString());
    }

    /// <summary>Backfill stays one batch, every reading in the device's order, each with its own time.</summary>
    [Fact]
    public void BufferedBackfill_KeepsEveryReadingInOrder()
    {
        var batch = GatewayV1Batch.From(Parse("valid-buffered-backfill.json"), Context);

        Assert.Equal(new[] { 1450.0, 1444.2, 1438.8 }, batch.Readings.Select(r => r.Value));
        Assert.Equal(
            new[] { "2026-09-13T18:00:04Z", "2026-09-13T22:00:06Z", "2026-09-14T02:00:09Z" }.Select(DateTimeOffset.Parse),
            batch.Readings.Select(r => r.At));
        Assert.Equal(new[] { "ok", "ok", "uncertain" }, batch.Readings.Select(r => r.Quality));
    }

    /// <summary>
    /// The device may omit quality; the gateway contract requires it. The platform resolves the
    /// default so a receiver never needs to know what it is.
    /// </summary>
    [Fact]
    public void OmittedQuality_IsSentAsOk()
    {
        var batch = GatewayV1Batch.From(Parse("valid-minimal.json"), Context);

        Assert.Equal("ok", Assert.Single(batch.Readings).Quality);
    }

    /// <summary>
    /// Data minimisation, CRA Annex I Part I point (g): battery and radio diagnostics, the device
    /// clock and the sequence number stay in the platform, and fields the device contract does
    /// not define are not passed through.
    /// </summary>
    [Theory]
    [InlineData("valid-full.json")]
    [InlineData("valid-unknown-fields.json")]
    public void OnlyTheContractsFields_AreSent(string fileName)
    {
        using var json = JsonDocument.Parse(GatewayV1Batch.From(Parse(fileName), Context).ToUtf8Json());

        Assert.Equal(
            new[] { "contractVersion", "deviceId", "messageId", "receivedAt", "firmware", "readings" },
            json.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.All(json.RootElement.GetProperty("readings").EnumerateArray(), reading =>
            Assert.Equal(
                new[] { "kind", "value", "unit", "at", "quality" },
                reading.EnumerateObject().Select(property => property.Name)));
    }

    /// <summary>
    /// A measurement the gateway contract cannot express is refused, not passed through: adding a
    /// type to the device contract must not change what receivers get without a decision here.
    /// </summary>
    [Fact]
    public void MeasurementWithoutAMapping_IsRefused()
    {
        var message = Parse("valid-minimal.json") with
        {
            Measurements =
            [
                new Measurement { Type = "temperature", Value = 12.5, Unit = "Cel", MeasuredAt = EnqueuedAt }
            ]
        };

        var exception = Assert.Throws<ForwardingException>(() => GatewayV1Batch.From(message, Context));

        Assert.Contains("'temperature'", exception.Message, StringComparison.Ordinal);
    }

    private static TelemetryMessage Parse(string fileName)
    {
        var result = TelemetryValidator.Validate(ContractFixtures.Read(fileName));
        Assert.True(result.IsValid, result.Error);
        return result.Message!;
    }
}
