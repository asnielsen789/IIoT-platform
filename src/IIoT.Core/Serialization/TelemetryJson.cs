using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IIoT.Core.Serialization;

/// <summary>
/// The single set of JSON options used for the telemetry contract, so the reader and any
/// future writer cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// The schema is the contract (ADR-004), and these options exist to read whatever it accepts.
/// System.Text.Json's defaults are narrower than JSON Schema in two places, and each is closed
/// here, once, rather than in the models or the validator:
/// </para>
/// <list type="bullet">
/// <item><b>Integers.</b> JSON Schema's <c>integer</c> is any number with a zero fractional part,
/// so <c>4711.0</c> and <c>4.711e3</c> are integers; the default converters accept only
/// <c>4711</c>. See <see cref="SchemaIntegerConverter{T}"/>.</item>
/// <item><b>Timestamps.</b> <c>"format": "date-time"</c> is RFC 3339, which allows a lower-case
/// <c>t</c> and <c>z</c> and a leap second; the default <see cref="DateTimeOffset"/> converter
/// refuses all three. See <see cref="Rfc3339TimestampConverter"/>.</item>
/// </list>
/// <para>
/// A value the schema accepts but the platform still cannot hold — an offset no time zone has, a
/// sequence number beyond <see cref="long.MaxValue"/> — is refused with a
/// <see cref="RejectedValueException"/> that says why, so it is rejected like any other invalid
/// message instead of surfacing as a deserialisation failure.
/// </para>
/// </remarks>
public static partial class TelemetryJson
{
    /// <summary>How much of an offending value a rejection quotes. The reason ends up in a log line.</summary>
    private const int MaxQuotedLength = 40;

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // The contract is camelCase; the models are PascalCase.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            // Case-sensitive, as the schema is. The schema validates "value"; to it, "Value" is an
            // unknown field and goes unchecked. Matched without regard to case, "Value": -50000
            // would have bound to the model in place of the "value" the schema had just approved.
            PropertyNameCaseInsensitive = false,

            // One value per member, or the schema and the model can read different ones. The
            // validator refuses a repeated name when it parses the document; this keeps the options
            // safe on their own.
            AllowDuplicateProperties = false,

            // Unknown fields are ignored, not rejected — evolution rules, section 6.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new Rfc3339TimestampConverter());
        options.Converters.Add(new SchemaIntegerConverter<int>());
        options.Converters.Add(new SchemaIntegerConverter<long>());
        return options;
    }

    private static string Quote(string value) =>
        value.Length <= MaxQuotedLength ? value : value[..MaxQuotedLength] + "...";

    /// <summary>
    /// Reads a JSON Schema <c>integer</c>: any number whose value is whole, however it is written.
    /// </summary>
    /// <remarks>
    /// The value is read as a <see cref="decimal"/>, which holds every <see cref="long"/> exactly, and
    /// then narrowed. Whole but too large for <typeparamref name="T"/> is refused with the range,
    /// never wrapped or truncated: a wrapped sequence number would look like a reset counter.
    /// </remarks>
    private sealed class SchemaIntegerConverter<T> : JsonConverter<T>
        where T : struct, IBinaryInteger<T>, IMinMaxValue<T>
    {
        private static readonly decimal Min = decimal.CreateChecked(T.MinValue);
        private static readonly decimal Max = decimal.CreateChecked(T.MaxValue);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                // Not a case the schema lets through, so this is a model that no longer matches it.
                // The serializer's own message, with the path, says so.
                throw new JsonException();
            }

            if (reader.TryGetDecimal(out var value) && decimal.IsInteger(value) && value >= Min && value <= Max)
            {
                return T.CreateChecked(value);
            }

            var text = Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
            throw new RejectedValueException(string.Create(
                CultureInfo.InvariantCulture,
                $"{Quote(text)} is not a whole number from {T.MinValue} to {T.MaxValue}, the range the platform holds this field in"));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(decimal.CreateChecked(value));
    }

    /// <summary>
    /// Reads an RFC 3339 <c>date-time</c> (section 5.6), which is what the schema's
    /// <c>"format": "date-time"</c> means.
    /// </summary>
    /// <remarks>
    /// <para>What is accepted beyond the default converter, and why:</para>
    /// <list type="bullet">
    /// <item><b>Lower-case <c>t</c> and <c>z</c>.</b> Section 5.6 allows them explicitly.</item>
    /// <item><b>A leap second</b>, <c>23:59:60</c> UTC, which section 5.6 allows and which the schema
    /// lets through only at that minute. <see cref="DateTimeOffset"/> has no 61st second, so
    /// <c>:60</c>, with any fraction, becomes <c>:59.9999999</c> — the last instant it can
    /// represent before the next minute. Rounding up to <c>:00</c> instead would move the reading
    /// into the next day. Ordering is kept: nothing in the leap second sorts before a reading
    /// taken in the second before it.</item>
    /// <item><b>Any number of fraction digits.</b> Kept to the 100 ns that
    /// <see cref="DateTimeOffset"/> resolves, by truncation rather than rounding, so a reading
    /// never moves into the next second.</item>
    /// </list>
    /// <para>
    /// What is refused, with the reason: a separator other than <c>T</c>/<c>t</c> (JsonSchema.Net
    /// also lets a space and an underscore through, which RFC 3339's grammar does not have), an
    /// RFC 9557 suffix such as <c>[Europe/Copenhagen]</c> (JsonSchema.Net accepts those under draft
    /// 2020-12 from version 9.5), an offset beyond ±14:00, which no time zone uses and
    /// <see cref="DateTimeOffset"/> cannot hold, and an instant outside the years 0001 to 9999 UTC.
    /// An old timestamp is never refused for being old: that is buffered backfill.
    /// </para>
    /// <para>
    /// <c>-00:00</c>, RFC 3339's "offset unknown" (section 4.3), names the instant in UTC, and is
    /// read as <c>+00:00</c>.
    /// </para>
    /// </remarks>
    private sealed partial class Rfc3339TimestampConverter : JsonConverter<DateTimeOffset>
    {
        private const int FractionDigits = 7;

        private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException();
            }

            var text = reader.GetString()!;
            var match = DateTimePattern().Match(text);
            if (!match.Success)
            {
                throw new RejectedValueException(
                    $"'{Quote(text)}' is not an RFC 3339 date-time: yyyy-mm-ddThh:mm:ss, an optional fraction, then Z or ±hh:mm");
            }

            var offset = TimeSpan.Zero;
            if (!match.Groups["utc"].Success)
            {
                var (offsetHour, offsetMinute) = (Number(match, "offsetHour"), Number(match, "offsetMinute"));
                if (offsetHour > 23 || offsetMinute > 59)
                {
                    throw new RejectedValueException($"'{Quote(text)}' has an offset that is not a time of day");
                }

                offset = new TimeSpan(offsetHour, offsetMinute, 0);
                if (match.Groups["sign"].Value == "-")
                {
                    offset = -offset;
                }
            }

            if (offset.Duration() > MaxOffset)
            {
                throw new RejectedValueException(
                    $"'{Quote(text)}' has a UTC offset beyond ±14:00, which no time zone uses and the platform cannot represent");
            }

            var second = Number(match, "second");
            var isLeapSecond = second == 60;
            var fractionTicks = isLeapSecond ? TimeSpan.TicksPerSecond - 1 : FractionTicks(match.Groups["fraction"].Value);

            DateTime local;
            try
            {
                local = new DateTime(
                    Number(match, "year"), Number(match, "month"), Number(match, "day"),
                    Number(match, "hour"), Number(match, "minute"), isLeapSecond ? 59 : second,
                    DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new RejectedValueException(
                    $"'{Quote(text)}' is not a calendar date and time between the years 0001 and 9999");
            }

            local = local.AddTicks(fractionTicks);

            // DateTimeOffset requires the instant itself, not only the local time, to fall within
            // DateTime's range: 0001-01-01T00:30:00+01:00 is a valid local time and an impossible
            // instant.
            var utcTicks = local.Ticks - offset.Ticks;
            if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            {
                throw new RejectedValueException(
                    $"'{Quote(text)}' lies outside 0001-01-01T00:00:00Z to 9999-12-31T23:59:59.9999999Z, the range the platform can represent");
            }

            return new DateTimeOffset(local, offset);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);

        /// <summary>
        /// The grammar of RFC 3339 section 5.6, with the lower-case letters its note allows.
        /// <c>[0-9]</c> rather than <c>\d</c>, which would also match digits from other scripts, and
        /// <c>\z</c> rather than <c>$</c>, which would also match before a trailing newline.
        /// </summary>
        [GeneratedRegex(
            @"^(?<year>[0-9]{4})-(?<month>[0-9]{2})-(?<day>[0-9]{2})[Tt]" +
            @"(?<hour>[0-9]{2}):(?<minute>[0-9]{2}):(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]+))?" +
            @"(?:(?<utc>[Zz])|(?<sign>[+-])(?<offsetHour>[0-9]{2}):(?<offsetMinute>[0-9]{2}))\z",
            RegexOptions.CultureInvariant)]
        private static partial Regex DateTimePattern();

        private static int Number(Match match, string group) =>
            int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

        private static long FractionTicks(string digits)
        {
            if (digits.Length == 0)
            {
                return 0;
            }

            var resolved = digits.Length >= FractionDigits ? digits[..FractionDigits] : digits.PadRight(FractionDigits, '0');
            return long.Parse(resolved, NumberStyles.None, CultureInfo.InvariantCulture);
        }
    }
}
