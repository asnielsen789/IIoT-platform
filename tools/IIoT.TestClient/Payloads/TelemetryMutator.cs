using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IIoT.TestClient.Payloads;

/// <summary>
/// Rewrites the handful of fields that make a replayed fixture a fresh, acceptable message.
/// </summary>
/// <remarks>
/// <para>
/// The edit is done on the JSON tree, not through the platform's models, so unknown fields survive
/// untouched and nothing the contract does not mention is normalised away. That matters for
/// <c>valid-unknown-fields.json</c>, whose whole point is that unknown fields pass through.
/// </para>
/// <para>
/// Four fields change:
/// </para>
/// <list type="bullet">
/// <item><c>messageId</c> — a new value per message, or the consumer may treat a resend as a duplicate.</item>
/// <item><c>sentAt</c> — now.</item>
/// <item><c>deviceId</c> — the authenticated device id. See the remark below; this one is not optional.</item>
/// <item><c>measurements[].measuredAt</c> — shifted by one constant offset.</item>
/// </list>
/// <para>
/// <strong>Why <c>deviceId</c> must be rewritten.</strong> Section 4 of the contract makes the
/// payload's <c>deviceId</c> non-authoritative: the consumer cross-checks it against the IoT Hub
/// system property <c>iothub-connection-device-id</c> and rejects a mismatch as a security event.
/// Two fixtures carry a hard-coded <c>tank-000123</c>, so without this rewrite every message from a
/// device registered under any other name would be correctly rejected, and the rejection would look
/// like a bug in the pipeline rather than in the test data.
/// </para>
/// <para>
/// <strong>Why one constant offset.</strong> Every <c>measuredAt</c> moves by the same amount, so
/// the relative spacing between readings survives and their relationship to <c>sentAt</c> survives
/// with it. <c>valid-buffered-backfill.json</c> therefore still looks like genuine backfill: three
/// readings four hours apart, the newest still older than <c>sentAt</c>. Shifting each timestamp
/// to "now" independently would flatten exactly the shape that fixture exists to exercise.
/// </para>
/// </remarks>
public sealed class TelemetryMutator
{
    /// <summary>
    /// Second precision, matching the contract's examples. Both the shift and the formatting
    /// round to whole seconds, so a shifted timestamp is never silently truncated.
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private const string MessageIdProperty = "messageId";
    private const string DeviceIdProperty = "deviceId";
    private const string SentAtProperty = "sentAt";
    private const string MeasurementsProperty = "measurements";
    private const string MeasuredAtProperty = "measuredAt";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _messageIdFactory;

    /// <param name="clock">Source of "now". Injected so the mutation is testable.</param>
    /// <param name="messageIdFactory">Source of fresh message ids.</param>
    public TelemetryMutator(Func<DateTimeOffset>? clock = null, Func<string>? messageIdFactory = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _messageIdFactory = messageIdFactory ?? MessageId.New;
    }

    /// <summary>Returns a mutated copy of <paramref name="payload"/> as UTF-8 bytes.</summary>
    /// <param name="payload">The fixture's bytes.</param>
    /// <param name="deviceId">The authenticated device id to stamp into the body.</param>
    public byte[] Mutate(byte[] payload, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (JsonNode.Parse(payload) is not JsonObject root)
        {
            throw new InvalidOperationException(
                "Only a JSON object payload can be mutated. Send it with --no-mutate instead.");
        }

        var now = TruncateToSecond(_clock());

        // Read the reference before overwriting sentAt, or the offset would always be zero.
        var offset = ReadReferenceInstant(root) is { } reference ? now - reference : TimeSpan.Zero;

        root[MessageIdProperty] = _messageIdFactory();
        root[DeviceIdProperty] = deviceId;
        root[SentAtProperty] = Format(now);

        if (root[MeasurementsProperty] is JsonArray measurements)
        {
            foreach (var measurement in measurements.OfType<JsonObject>())
            {
                if (TryReadInstant(measurement[MeasuredAtProperty], out var measuredAt))
                {
                    measurement[MeasuredAtProperty] = Format(measuredAt + offset);
                }
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);
    }

    /// <summary>
    /// The instant the shift is measured from: the original <c>sentAt</c>, or failing that the
    /// newest parsable <c>measuredAt</c>. An invalid fixture may have neither, in which case the
    /// timestamps are left where they are rather than guessed at — it is going to be rejected on
    /// its own terms anyway.
    /// </summary>
    private static DateTimeOffset? ReadReferenceInstant(JsonObject root)
    {
        if (TryReadInstant(root[SentAtProperty], out var sentAt))
            return sentAt;

        if (root[MeasurementsProperty] is not JsonArray measurements)
            return null;

        DateTimeOffset? newest = null;
        foreach (var measurement in measurements.OfType<JsonObject>())
        {
            if (TryReadInstant(measurement[MeasuredAtProperty], out var measuredAt) &&
                (newest is null || measuredAt > newest))
            {
                newest = measuredAt;
            }
        }

        return newest;
    }

    private static bool TryReadInstant(JsonNode? node, out DateTimeOffset instant)
    {
        instant = default;

        return node is JsonValue value &&
               value.TryGetValue<string>(out var text) &&
               DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out instant);
    }

    /// <summary>
    /// Rounding "now" down to a whole second keeps the offset a whole number of seconds, so no
    /// shifted timestamp loses sub-second precision when it is formatted and the spacing between
    /// readings is preserved exactly rather than approximately.
    /// </summary>
    private static DateTimeOffset TruncateToSecond(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
