using System.Text.Json;
using System.Text.Json.Serialization;
using IIoT.Core.Models;

namespace IIoT.Core.Forwarders;

/// <summary>
/// The body of <c>POST /v1/readings</c>, as defined by
/// <c>docs/schemas/gateway/readings-v1.schema.json</c> and <c>docs/gateway-contract.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wire names are fixed with attributes rather than derived from a naming policy, so renaming a
/// property here can never silently change the contract.
/// </para>
/// <para>
/// This is where the device contract is mapped onto the gateway contract, and the mapping is
/// deliberately explicit. Identity and receipt time come from the <see cref="ForwardingContext"/>
/// — what the platform proved — and never from the payload. Device fields the gateway has no
/// stated need for (battery, signal, <c>sentAt</c>, <c>sequence</c>) are not carried over, and a
/// measurement type the gateway contract does not define is refused rather than passed through:
/// a change to the device contract reaches the gateway only when this mapping is changed.
/// </para>
/// </remarks>
public sealed record GatewayV1Batch
{
    /// <summary>The only version <c>/v1</c> carries.</summary>
    public const int ContractVersionV1 = 1;

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
    public required IReadOnlyList<GatewayV1Reading> Readings { get; init; }

    /// <summary>Maps a validated device message onto the gateway contract.</summary>
    /// <exception cref="ForwardingException">
    /// The message holds a measurement the gateway contract has no mapping for.
    /// </exception>
    public static GatewayV1Batch From(TelemetryMessage message, ForwardingContext context) => new()
    {
        ContractVersion = ContractVersionV1,
        DeviceId = context.AuthenticatedDeviceId,
        MessageId = message.MessageId,
        ReceivedAt = context.EnqueuedAt.ToUniversalTime(),
        Firmware = message.Device.Firmware,

        // Every measurement, in the order the device sent them: one batch, never split (ADR-004).
        Readings = message.Measurements.Select(ToReading).ToList()
    };

    /// <summary>The exact bytes the forwarder sends.</summary>
    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    private static GatewayV1Reading ToReading(Measurement measurement)
    {
        var (kind, unit) = (measurement.Type, measurement.Unit) switch
        {
            ("level", "mm") => ("level", "mm"),
            _ => throw new ForwardingException(
                $"Measurement type '{measurement.Type}' in '{measurement.Unit}' has no mapping in gateway contract v1")
        };

        return new GatewayV1Reading
        {
            Kind = kind,
            Value = measurement.Value,
            Unit = unit,

            // Device time, kept with the offset the device sent: it is the time of measurement,
            // and the receiver must be able to see exactly what the device claimed.
            At = measurement.MeasuredAt,

            Quality = measurement.Quality switch
            {
                MeasurementQuality.Ok => "ok",
                MeasurementQuality.Uncertain => "uncertain",
                MeasurementQuality.Bad => "bad",
                _ => throw new ForwardingException(
                    $"Measurement quality '{measurement.Quality}' has no mapping in gateway contract v1")
            }
        };
    }
}

/// <summary>One reading in a <see cref="GatewayV1Batch"/>.</summary>
public sealed record GatewayV1Reading
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("value")]
    public required double Value { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }

    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>Always present: a quality the device omitted has already defaulted to <c>ok</c>.</summary>
    [JsonPropertyName("quality")]
    public required string Quality { get; init; }
}
