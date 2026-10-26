using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Validation;

namespace IIoT.Core.Tests.Validation;

/// <summary>
/// Payloads the published v1 schema accepts that the models once could not read. Each one is now
/// either deserialised or rejected with a reason naming the field — never left to a
/// deserialisation exception.
/// </summary>
/// <remarks>
/// <para>
/// The schema is the contract (ADR-004), and v1 is not tightened: a device that conforms to the
/// published document must not start being rejected. Every case here was confirmed against the
/// real validator before it was fixed — each of these payloads passes the schema.
/// </para>
/// <para>
/// Before the fix, every one of them ended as <c>RejectedInvalidPayload</c> with the reason
/// "Payload passed schema validation but could not be deserialised", so a conforming device's
/// message was dropped and logged as if the device were at fault.
/// </para>
/// </remarks>
public class SchemaAgreementTests
{
    // --- sequence: the schema says integer >= 0, with no maximum ------------------------------

    /// <summary>
    /// Firmware counters are typically unsigned 32-bit. Every value from 2³¹ up — half of such a
    /// counter's range — used to fail, because the model held the field in an <c>int</c>.
    /// </summary>
    [Theory]
    [InlineData("2147483648", 2147483648L)]
    [InlineData("4294967295", 4294967295L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Sequence_BeyondInt32_IsDeserialised(string sequence, long expected)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(sequence: sequence));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(expected, result.Message!.Sequence);
    }

    /// <summary>
    /// The schema sets no maximum, so a value beyond what the platform holds still passes it. That
    /// is a rejection with the reason and the field, never an exception and never a wrapped value,
    /// which would look like a counter reset.
    /// </summary>
    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551616")]
    [InlineData("1e30")]
    public void Sequence_BeyondInt64_IsRejectedWithTheReason(string sequence)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(sequence: sequence));

        Assert.False(result.IsValid);
        Assert.StartsWith($"/sequence: {sequence} is not a whole number from", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be deserialised", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON Schema's <c>integer</c> is any number with a zero fractional part, so these are
    /// integers to the schema, and must be to the platform.
    /// </summary>
    [Theory]
    [InlineData("4711.0")]
    [InlineData("4.711e3")]
    [InlineData("4711E0")]
    public void Sequence_WholeNumberWrittenWithAFraction_IsDeserialised(string sequence)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(sequence: sequence));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(4711L, result.Message!.Sequence);
    }

    /// <summary>The same rule for the other integer fields.</summary>
    [Fact]
    public void Signal_WholeNumberWrittenWithAFraction_IsDeserialised()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(
            device: """{ "firmware": "1.4.2", "signal": { "rsrp": -104.0, "rsrq": -1.1e1 } }"""));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(-104, result.Message!.Device.Signal!.Rsrp);
        Assert.Equal(-11, result.Message.Device.Signal.Rsrq);
    }

    /// <summary>
    /// <c>"const": 1</c> compares by value, so <c>1.0</c> is version 1 to the schema. It used to be
    /// refused as an unsupported version — the one message section 6 says must never be guessed at.
    /// </summary>
    [Fact]
    public void SchemaVersion_OneWrittenWithAFraction_IsVersionOne()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(schemaVersion: "1.0"));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(1, result.Message!.SchemaVersion);
    }

    /// <summary>Comparing by value does not loosen the version check: 1.5 is not 1.</summary>
    [Fact]
    public void SchemaVersion_NotWhole_IsStillAnUnsupportedVersion()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(schemaVersion: "1.5"));

        Assert.False(result.IsValid);
        Assert.StartsWith("Unsupported schemaVersion '1.5'", result.Error, StringComparison.Ordinal);
    }

    // --- timestamps: "format": "date-time" is RFC 3339 section 5.6 ------------------------------

    /// <summary>RFC 3339 section 5.6: "T" and "Z" may alternatively be lower case.</summary>
    [Theory]
    [InlineData("2026-09-14t06:00:10Z")]
    [InlineData("2026-09-14T06:00:10z")]
    [InlineData("2026-09-14t06:00:10z")]
    public void Timestamp_LowerCaseTOrZ_IsTheSameInstant(string measuredAt)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: measuredAt, sentAt: measuredAt));

        Assert.True(result.IsValid, result.Error);
        var expected = new DateTimeOffset(2026, 9, 14, 6, 0, 10, TimeSpan.Zero);
        Assert.Equal(expected, result.Message!.SentAt);
        Assert.Equal(expected, Assert.Single(result.Message.Measurements).MeasuredAt);
    }

    /// <summary>
    /// A leap second has no representation in .NET, so it becomes the last instant before the next
    /// minute. Rounding up instead would move the reading into the next day — and into the next
    /// year, on this date.
    /// </summary>
    [Theory]
    [InlineData("2016-12-31T23:59:60Z")]
    [InlineData("2016-12-31T23:59:60.5Z")]
    public void Timestamp_LeapSecond_IsTheLastInstantOfTheMinute_OnTheSameDay(string measuredAt)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: measuredAt));

        Assert.True(result.IsValid, result.Error);
        var at = Assert.Single(result.Message!.Measurements).MeasuredAt;
        Assert.Equal(new DateTimeOffset(2016, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(TimeSpan.TicksPerSecond - 1), at);
        Assert.Equal(2016, at.Year);
    }

    /// <summary>
    /// The leap second is 23:59:60 UTC wherever the device is. With an offset, the local reading
    /// keeps its own date and offset; it is the instant that sits at the end of the UTC day.
    /// </summary>
    [Fact]
    public void Timestamp_LeapSecondWithAnOffset_KeepsTheDevicesDateAndOffset()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: "2017-01-01T00:59:60+01:00"));

        Assert.True(result.IsValid, result.Error);
        var at = Assert.Single(result.Message!.Measurements).MeasuredAt;
        Assert.Equal(TimeSpan.FromHours(1), at.Offset);
        Assert.Equal(new DateTime(2017, 1, 1, 0, 59, 59).AddTicks(TimeSpan.TicksPerSecond - 1), at.DateTime);
        Assert.Equal(new DateTime(2016, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(TimeSpan.TicksPerSecond - 1), at.UtcDateTime);
    }

    /// <summary>
    /// Buffered readings around a leap second stay in the order they were taken: the leap second
    /// sorts after the second before it and before the next minute.
    /// </summary>
    [Fact]
    public void Timestamp_LeapSecondInBackfill_KeepsTheOrder()
    {
        var payload = """
            {
              "schemaVersion": 1,
              "messageId": "01J8F2K9QS4T7V0X3Z6B8N1M5F",
              "sentAt": "2017-01-01T00:00:05Z",
              "measurements": [
                { "type": "level", "value": 1450.0, "unit": "mm", "measuredAt": "2016-12-31T23:59:59.5Z" },
                { "type": "level", "value": 1449.9, "unit": "mm", "measuredAt": "2016-12-31T23:59:60Z" },
                { "type": "level", "value": 1449.8, "unit": "mm", "measuredAt": "2017-01-01T00:00:00Z" }
              ],
              "device": { "firmware": "1.4.2" }
            }
            """;

        var result = TelemetryValidator.Validate(payload);

        Assert.True(result.IsValid, result.Error);
        var times = result.Message!.Measurements.Select(m => m.MeasuredAt).ToList();
        Assert.True(times[0] < times[1] && times[1] < times[2], string.Join(", ", times.Select(t => t.ToString("O"))));
    }

    /// <summary>
    /// The highest instant there is still holds a leap second: it maps onto
    /// <see cref="DateTimeOffset.MaxValue"/> rather than past it.
    /// </summary>
    [Fact]
    public void Timestamp_LeapSecondAtTheEndOfTime_IsTheLastRepresentableInstant()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: "9999-12-31T23:59:60Z"));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(result.Message!.Measurements).MeasuredAt);
    }

    /// <summary>
    /// RFC 3339 allows any number of fraction digits; .NET resolves 100 ns. The rest is truncated,
    /// not rounded, so a reading never moves into the next second.
    /// </summary>
    [Theory]
    [InlineData("2026-09-14T06:00:10.123456789012345678901234567890Z", 1234567)]
    [InlineData("2026-09-14T06:00:10.99999999Z", 9999999)]
    [InlineData("2026-09-14T06:00:10.5Z", 5000000)]
    public void Timestamp_AnyNumberOfFractionDigits_IsTruncatedTo100Nanoseconds(string measuredAt, long ticks)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: measuredAt));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 6, 0, 10, TimeSpan.Zero).AddTicks(ticks), Assert.Single(result.Message!.Measurements).MeasuredAt);
    }

    /// <summary>
    /// ±14:00 is the widest offset any time zone uses and the widest <see cref="DateTimeOffset"/>
    /// holds. Both ends are kept, with the offset the device sent.
    /// </summary>
    [Theory]
    [InlineData("2026-09-14T06:00:10+14:00", 14)]
    [InlineData("2026-09-14T06:00:10-14:00", -14)]
    public void Timestamp_WidestRealOffset_IsKept(string measuredAt, int offsetHours)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: measuredAt));

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(TimeSpan.FromHours(offsetHours), Assert.Single(result.Message!.Measurements).MeasuredAt.Offset);
    }

    /// <summary>
    /// The trust model: a reading is never rejected for being old. That holds for the new parser at
    /// the far end of the range too — a device without a set clock reports 1970 or 2000.
    /// </summary>
    [Theory]
    [InlineData("1970-01-01T00:00:00Z")]
    [InlineData("2000-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    public void Timestamp_FarInThePast_IsNeverRejected(string measuredAt)
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: measuredAt));

        Assert.True(result.IsValid, result.Error);
    }

    public static TheoryData<string, string> UnrepresentableTimestamps => new()
    {
        // RFC 3339 allows offset hours to 23; no time zone goes beyond 14, and nor does .NET.
        { "2026-09-14T06:00:10+23:59", "has a UTC offset beyond ±14:00" },
        { "2026-09-14T06:00:10+14:01", "has a UTC offset beyond ±14:00" },

        // RFC 3339's four-digit year starts at 0000; .NET's calendar starts at 0001.
        { "0000-01-01T00:00:00Z", "is not a calendar date and time between the years 0001 and 9999" },

        // Valid local times whose instants fall outside .NET's range.
        { "0001-01-01T00:30:00+01:00", "lies outside 0001-01-01T00:00:00Z to 9999-12-31T23:59:59.9999999Z" },
        { "9999-12-31T23:30:00-01:00", "lies outside 0001-01-01T00:00:00Z to 9999-12-31T23:59:59.9999999Z" },

        // JsonSchema.Net lets these through; RFC 3339's grammar has none of them.
        { "2026-09-14 06:00:10Z", "is not an RFC 3339 date-time" },
        { "2026-09-14_06:00:10Z", "is not an RFC 3339 date-time" }
    };

    /// <summary>
    /// Each of these passes the schema's <c>date-time</c> check but cannot be held as a
    /// <see cref="DateTimeOffset"/>, or is not RFC 3339 at all. A rejection with the field and the
    /// reason, like a schema error — never an exception, and never the generic
    /// "could not be deserialised".
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrepresentableTimestamps))]
    public void Timestamp_SchemaValidButUnrepresentable_IsRejectedWithTheReason(string measuredAt, string reason)
    {
        var payload = InlinePayload.Telemetry(measuredAt: measuredAt.Replace("\n", "\\n", StringComparison.Ordinal));

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.StartsWith("/measurements/0/measuredAt: '", result.Error, StringComparison.Ordinal);
        Assert.Contains(reason, result.Error!, StringComparison.Ordinal);
    }

    public static TheoryData<string> NotRfc3339WhicheverLayerRefuses => new()
    {
        // JsonSchema.Net up to 9.4 ended its date-time pattern with $, which also matches before a
        // final newline, so the schema let this through and the reader refused it. 9.5 anchors with
        // \z and refuses it itself.
        "2026-09-14T06:00:10Z\n",

        // RFC 9557 suffixes: a time zone name, an offset, a calendar tag. JsonSchema.Net up to 9.4
        // refused them; 9.5 accepts them under draft 2020-12, so the reader refuses them instead.
        "2026-09-14T06:00:10Z[Europe/Copenhagen]",
        "2026-09-14T08:00:10+02:00[!+02:00]",
        "2026-09-14T06:00:10Z[u-ca=iso8601]"
    };

    /// <summary>
    /// None of these is an RFC 3339 date-time, but which layer refuses each one is up to the version
    /// of the schema library: the schema's <c>date-time</c> check, or <see cref="TelemetryJson"/>'s
    /// reader behind it. The contract is the outcome, not the layer: a rejection that names the field,
    /// never an exception and never a message processed with a timestamp the platform misread.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotRfc3339WhicheverLayerRefuses))]
    public void Timestamp_NotRfc3339_IsRejectedNamingTheField_WhicheverLayerRefuses(string measuredAt)
    {
        var payload = InlinePayload.Telemetry(measuredAt: measuredAt.Replace("\n", "\\n", StringComparison.Ordinal));

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.StartsWith("/measurements/0/measuredAt: ", result.Error, StringComparison.Ordinal);
    }

    /// <summary>The same holds for <c>sentAt</c>, and the reason names that field.</summary>
    [Fact]
    public void Timestamp_UnrepresentableSentAt_NamesTheField()
    {
        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(sentAt: "2026-09-14T06:00:12+20:00"));

        Assert.False(result.IsValid);
        Assert.StartsWith("/sentAt: '2026-09-14T06:00:12+20:00' has a UTC offset beyond ±14:00", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A long value is quoted only in part: the reason ends up in a log line, and a device decides
    /// how long a fraction is.
    /// </summary>
    [Fact]
    public void Rejection_QuotesOnlyTheStartOfALongValue()
    {
        var measuredAt = "2026-09-14 06:00:10." + new string('9', 5000) + "Z";

        var result = TelemetryValidator.Validate(InlinePayload.Telemetry(measuredAt: measuredAt));

        Assert.False(result.IsValid);
        Assert.True(result.Error!.Length < 200, $"the reason is {result.Error.Length} characters long");
    }

    // --- strings .NET cannot hold -------------------------------------------------------------

    /// <summary>
    /// An escaped lone surrogate is well-formed JSON (RFC 8259 section 8.2) but no string to .NET,
    /// and the schema evaluator throws when it reads one to check its length. That used to escape
    /// the validator and fail the whole function invocation; it is a rejection now.
    /// </summary>
    [Theory]
    [InlineData("messageId")]
    [InlineData("firmware")]
    public void String_WithAnEscapedLoneSurrogate_IsRejectedWithoutThrowing(string field)
    {
        // Raw string literals: the payload carries the six characters \uD800, not the code unit.
        var payload = field == "messageId"
            ? InlinePayload.Telemetry().Replace("01J8F2K9QS4T7V0X3Z6B8N1M5E", """\uD800""", StringComparison.Ordinal)
            : InlinePayload.Telemetry(device: """{ "firmware": "\uDC00" }""");

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.StartsWith("Payload could not be evaluated against the v1 schema", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The validator takes any string, and one holding an unpaired surrogate cannot be parsed at
    /// all. A body decoded from the wire never does, but the method must not throw for it either.
    /// </summary>
    [Fact]
    public void Payload_WithAnUnpairedSurrogateCharacter_IsRejectedWithoutThrowing()
    {
        var payload = InlinePayload.Telemetry().Replace("01J8F2K9QS4T7V0X3Z6B8N1M5E", "\uD800", StringComparison.Ordinal);

        var result = TelemetryValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.StartsWith("Payload is not valid text", result.Error, StringComparison.Ordinal);
    }
}
