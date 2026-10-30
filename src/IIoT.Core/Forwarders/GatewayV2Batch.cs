using System.Text.Json;
using System.Text.Json.Serialization;
using IIoT.Core.Models;

namespace IIoT.Core.Forwarders;

/// <summary>
/// The body of <c>POST /v2/readings</c>, as defined by
/// <c>docs/schemas/gateway/readings-v2.schema.json</c> and <c>docs/gateway-contract.md</c>, section 11
/// (ADR-011): v1's batch, with every reading kind of the device contract and each reading's sensor.
/// </summary>
/// <remarks>
/// <para>
/// Wire names are fixed with attributes rather than derived from a naming policy, so renaming a
/// property here can never silently change the contract.
/// </para>
/// <para>
/// As for v1, the mapping is explicit. Identity and receipt time come from the
/// <see cref="ForwardingContext"/> — what the platform proved — and never from the payload; the
/// same device fields are left out; and a type the contract does not define is refused rather than
/// passed through, so a change to the device contract reaches the gateway only when this mapping is
/// changed. Each type keeps its unit: the device schema has already tied them together, and the map
/// below checks the pair again rather than trusting it.
/// </para>
/// </remarks>
public sealed record GatewayV2Batch
{
    /// <summary>The only version <c>/v2</c> carries.</summary>
    public const int ContractVersionV2 = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    [JsonPropertyName("contractVersion")]
    public required int ContractVersion { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("receivedAt")]
    public required DateTimeOffset ReceivedAt { get; init; }

    [JsonPropertyName("firmware")]
    public required string Firmware { get; init; }

    [JsonPropertyName("readings")]
    public required IReadOnlyList<GatewayV2Reading> Readings { get; init; }

    /// <summary>Maps a validated device message onto gateway contract v2.</summary>
    /// <exception cref="ForwardingException">
    /// The message holds a measurement the gateway contract has no mapping for.
    /// </exception>
    public static GatewayV2Batch From(TelemetryMessage message, ForwardingContext context) => new()
    {
        ContractVersion = ContractVersionV2,
        DeviceId = context.AuthenticatedDeviceId,
        MessageId = message.MessageId,
        ReceivedAt = context.EnqueuedAt.ToUniversalTime(),
        Firmware = message.Device.Firmware,

        // Every measurement, in the order the device sent them: one batch, never split (ADR-004),
        // whether its readings are backfill or several sensors read at once.
        Readings = message.Measurements.Select(ToReading).ToList()
    };

    /// <summary>The exact bytes the forwarder sends.</summary>
    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    private static GatewayV2Reading ToReading(Measurement measurement)
    {
        var (kind, unit) = (measurement.Type, measurement.Unit) switch
        {
            ("level", "mm") => ("level", "mm"),
            ("energy", "kW.h") => ("energy", "kW.h"),
            ("power", "kW") => ("power", "kW"),
            ("volume", "L") => ("volume", "L"),
            ("count", "1") => ("count", "1"),
            _ => throw new ForwardingException(
                $"Measurement type '{measurement.Type}' in '{measurement.Unit}' has no mapping in gateway contract v2")
        };

        return new GatewayV2Reading
        {
            Kind = kind,
            Value = measurement.Value,
            Unit = unit,
            Sensor = measurement.Sensor,

            // Device time, kept with the offset the device sent: it is the time of measurement,
            // and the receiver must be able to see exactly what the device claimed.
            At = measurement.MeasuredAt,

            Quality = measurement.Quality switch
            {
                MeasurementQuality.Ok => "ok",
                MeasurementQuality.Uncertain => "uncertain",
                MeasurementQuality.Bad => "bad",
                _ => throw new ForwardingException(
                    $"Measurement quality '{measurement.Quality}' has no mapping in gateway contract v2")
            }
        };
    }
}

/// <summary>One reading in a <see cref="GatewayV2Batch"/>.</summary>
public sealed record GatewayV2Reading
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// A <see cref="double"/>, as on v1. A <c>count</c> is an integer up to 2⁵³ − 1 (the device
    /// schema's bound), which a double holds exactly, and which is written without a fraction.
    /// </summary>
    [JsonPropertyName("value")]
    public required double Value { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }

    /// <summary>Left out when the device named none, as the contract has it.</summary>
    [JsonPropertyName("sensor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sensor { get; init; }

    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>Always present: a quality the device omitted has already defaulted to <c>ok</c>.</summary>
    [JsonPropertyName("quality")]
    public required string Quality { get; init; }
}
