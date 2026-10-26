using System.Text;
using System.Text.Json;
using IIoT.Core.DeadLettering;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Validation;

namespace IIoT.Core.Tests.DeadLettering;

public class DeadLetterEnvelopeTests
{
    private const string DeviceId = "tank-000123";
    private const string MessageId = "01J8F2K9QS4T7V0X3Z6B8N1M5B";

    private static readonly DateTimeOffset EnqueuedAt = new(2026, 9, 14, 6, 0, 13, 250, TimeSpan.Zero);
    private static readonly DateTimeOffset DeadLetteredAt = new(2026, 9, 14, 6, 0, 44, 125, TimeSpan.Zero);

    private static DeadLetterEnvelope Envelope(string payload, string reason = "Gateway v1 responded 503 ServiceUnavailable", int? statusCode = 503) =>
        DeadLetterEnvelope.Create(payload, DeviceId, EnqueuedAt, MessageId, reason, statusCode, DeadLetteredAt);

    [Fact]
    public void Envelope_RoundTrips_WithEveryField()
    {
        var envelope = Envelope(ContractFixtures.Read("valid-full.json"));

        var read = DeadLetterEnvelopeJson.Read(DeadLetterEnvelopeJson.Serialize(envelope));

        Assert.True(read.IsValid, read.Error);
        Assert.Equal(envelope, read.Envelope);
        Assert.Equal(DeadLetterEnvelope.CurrentVersion, read.Envelope!.EnvelopeVersion);
    }

    /// <summary>
    /// "Exactly as received" is character for character: the fixture's indentation and line breaks,
    /// a UTC offset's <c>+</c>, non-ASCII text and an escape sequence all come back unchanged.
    /// </summary>
    [Theory]
    [InlineData("valid-full.json")]
    [InlineData("valid-buffered-backfill.json")]
    [InlineData("valid-unknown-fields.json")]
    public void Payload_IsKeptCharacterForCharacter(string fixture)
    {
        var payload = ContractFixtures.Read(fixture) + "\r\n  ";

        var read = DeadLetterEnvelopeJson.Read(DeadLetterEnvelopeJson.Serialize(Envelope(payload)));

        Assert.Equal(payload, read.Envelope!.Payload);
    }

    [Fact]
    public void Payload_WithEscapesAndNonAsciiText_IsKeptCharacterForCharacter()
    {
        const string payload = """{"messageId":"a\"b\\c\u0001","site":"Tårnby","at":"2026-09-14T08:00:10+02:00","emoji":"🛢"}""";

        var entry = DeadLetterEnvelopeJson.Serialize(Envelope(payload));

        Assert.Equal(payload, DeadLetterEnvelopeJson.Read(entry).Envelope!.Payload);

        // Readable for an operator: the relaxed encoder leaves a + and non-ASCII letters alone.
        Assert.Contains("+02:00", entry, StringComparison.Ordinal);
        Assert.Contains("Tårnby", entry, StringComparison.Ordinal);
    }

    /// <summary>The wire names are the envelope's contract with every later build and with an operator.</summary>
    [Fact]
    public void Entry_UsesTheDocumentedFieldNames()
    {
        var payload = ContractFixtures.Read("valid-minimal.json");

        using var document = JsonDocument.Parse(DeadLetterEnvelopeJson.Serialize(Envelope(payload)));
        var root = document.RootElement;

        Assert.Equal(
            new[] { "envelopeVersion", "messageId", "authenticatedDeviceId", "enqueuedAt", "deadLetteredAt", "failure", "payload" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("envelopeVersion").GetInt32());
        Assert.Equal(MessageId, root.GetProperty("messageId").GetString());
        Assert.Equal(DeviceId, root.GetProperty("authenticatedDeviceId").GetString());
        Assert.Equal(EnqueuedAt, root.GetProperty("enqueuedAt").GetDateTimeOffset());
        Assert.Equal(DeadLetteredAt, root.GetProperty("deadLetteredAt").GetDateTimeOffset());
        Assert.Equal("Gateway v1 responded 503 ServiceUnavailable", root.GetProperty("failure").GetProperty("reason").GetString());
        Assert.Equal(503, root.GetProperty("failure").GetProperty("statusCode").GetInt32());

        // A string, not a nested object: re-serialising an object would not keep it exactly.
        Assert.Equal(JsonValueKind.String, root.GetProperty("payload").ValueKind);
        Assert.Equal(payload, root.GetProperty("payload").GetString());
    }

    /// <summary>A failure that never reached the gateway has no status; the field is left out, and reads back as absent.</summary>
    [Fact]
    public void StatusCode_WhenAbsent_IsLeftOutAndReadsBackAsNull()
    {
        var entry = DeadLetterEnvelopeJson.Serialize(Envelope("{}", "Connection refused", statusCode: null));

        Assert.DoesNotContain("statusCode", entry, StringComparison.Ordinal);
        Assert.Null(DeadLetterEnvelopeJson.Read(entry).Envelope!.Failure.StatusCode);
    }

    [Fact]
    public void Reason_IsCutToItsLimit()
    {
        var envelope = Envelope("{}", new string('x', DeadLetterEnvelope.MaxReasonLength + 500));

        Assert.Equal(new string('x', DeadLetterEnvelope.MaxReasonLength) + "...", envelope.Failure.Reason);
    }

    /// <summary>
    /// 64 KiB of queue message, base64-encoded by the trigger's default, holds 48 KiB of entry. The
    /// limit is the largest entry whose base64 form still fits, and one byte more does not.
    /// </summary>
    [Fact]
    public void SizeLimit_IsTheLargestEntryWhoseBase64FitsInOneQueueMessage()
    {
        Assert.Equal(65_536, DeadLetterEnvelopeJson.MaxQueueMessageLength);
        Assert.Equal(49_152, DeadLetterEnvelopeJson.MaxEntryBytes);

        var largest = EntryOfSize(DeadLetterEnvelopeJson.MaxEntryBytes);
        var tooLarge = EntryOfSize(DeadLetterEnvelopeJson.MaxEntryBytes + 1);

        Assert.True(DeadLetterEnvelopeJson.FitsInQueueMessage(largest));
        Assert.Equal(DeadLetterEnvelopeJson.MaxQueueMessageLength, Convert.ToBase64String(Encoding.UTF8.GetBytes(largest)).Length);

        Assert.False(DeadLetterEnvelopeJson.FitsInQueueMessage(tooLarge));
        Assert.True(Convert.ToBase64String(Encoding.UTF8.GetBytes(tooLarge)).Length > DeadLetterEnvelopeJson.MaxQueueMessageLength);
    }

    /// <summary>
    /// The largest message a v1 device plausibly sends: the schema's 64 measurements, each with every
    /// field at a realistic maximum length, the longest messageId and deviceId, and full metadata.
    /// Its entry fits with room to spare, so the limit only bites on a payload padded far beyond
    /// what the contract describes.
    /// </summary>
    [Fact]
    public void LargestRealisticMessage_FitsWithRoomToSpare()
    {
        var measurement = """
            {"type":"level","value":19999.999,"unit":"mm","measuredAt":"2026-09-14T06:00:10.1234567+00:00","quality":"uncertain"}
            """;
        var payload =
            $$"""{"schemaVersion":1,"messageId":"{{new string('M', 64)}}","deviceId":"{{new string('d', 128)}}",""" +
            """ "sentAt":"2026-09-14T06:00:12.1234567+00:00","sequence":4294967295,""" +
            $$""" "measurements":[{{string.Join(",", Enumerable.Repeat(measurement, 64))}}],""" +
            """ "device":{"firmware":"10.20.30-rc.4+build.5678","battery":{"value":3.6,"unit":"V"},"signal":{"rsrp":-140,"rsrq":-20}}}""";
        Assert.True(TelemetryValidator.Validate(payload).IsValid);

        var entry = DeadLetterEnvelopeJson.Serialize(
            DeadLetterEnvelope.Create(payload, new string('d', 128), EnqueuedAt, new string('M', 64),
                new string('r', DeadLetterEnvelope.MaxReasonLength), 503, DeadLetteredAt));

        Assert.True(DeadLetterEnvelopeJson.FitsInQueueMessage(entry));
        Assert.True(DeadLetterEnvelopeJson.ByteCount(entry) < DeadLetterEnvelopeJson.MaxEntryBytes / 4,
            $"{DeadLetterEnvelopeJson.ByteCount(entry)} bytes");
    }

    /// <summary>
    /// The schema does not bound a message's size: unknown fields are allowed at every level, and a
    /// number or a timestamp's fraction may have any number of digits. Only IoT Hub does, at 256 KB.
    /// So a payload the platform accepts can produce an entry no queue message can hold.
    /// </summary>
    [Fact]
    public void ValidPayload_CanBeTooLargeForAQueueMessage()
    {
        var payload = InlinePayload.Telemetry(extraRootMember: $"\"padding\": \"{new string('p', 60_000)}\"");
        Assert.True(TelemetryValidator.Validate(payload).IsValid);

        Assert.False(DeadLetterEnvelopeJson.FitsInQueueMessage(DeadLetterEnvelopeJson.Serialize(Envelope(payload))));
    }

    /// <summary>
    /// Escaping is what makes the worst case worse than the payload's own size: a quote becomes two
    /// bytes, so a payload of quotes doubles. The limit counts the entry as written, so it holds.
    /// </summary>
    [Fact]
    public void ByteCount_CountsTheEscapedEntry()
    {
        var quotes = new string('"', 1000);

        var entry = DeadLetterEnvelopeJson.Serialize(Envelope(quotes));

        Assert.Equal(DeadLetterEnvelopeJson.ByteCount(DeadLetterEnvelopeJson.Serialize(Envelope(""))) + 2000,
            DeadLetterEnvelopeJson.ByteCount(entry));
    }

    public static TheoryData<string, string> UnreadableEntries => new()
    {
        { "", "Entry is empty" },
        { "not json", "Entry is not valid JSON" },
        { "[1, 2]", "Entry is not a JSON object" },
        { """{"messageId":"x"}""", "Entry has no envelopeVersion" },
        { """{"envelopeVersion":2,"messageId":"x"}""", "Unsupported envelopeVersion '2'; this build understands version 1" },
        { """{"envelopeVersion":"1"}""", "Unsupported envelopeVersion '\"1\"'" },
        { """{"envelopeVersion":1.5}""", "Unsupported envelopeVersion '1.5'" },
        { """{"envelopeVersion":1,"envelopeVersion":1}""", "Entry is not valid JSON" },
        { """{"envelopeVersion":1,"messageId":"x"}""", "Entry is not a valid version 1 envelope" }
    };

    /// <summary>
    /// Whatever is wrong with an entry, reading it ends in a reason, never an exception: the retry
    /// function moves an unreadable entry to the poison queue instead of failing on it forever.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableEntries))]
    public void Read_UnreadableEntry_IsRefusedWithAReason(string entry, string reason)
    {
        var read = DeadLetterEnvelopeJson.Read(entry);

        Assert.False(read.IsValid);
        Assert.StartsWith(reason, read.Error, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> UnusableFields => new()
    {
        { "payload", "null", "Entry is not a valid version 1 envelope" },
        { "payload", "\"\"", "Entry has an empty payload" },
        { "authenticatedDeviceId", "\"  \"", "Entry has an empty authenticatedDeviceId" },
        { "messageId", "\"\"", "Entry has an empty messageId" },
        { "enqueuedAt", "\"yesterday\"", "Entry is not a valid version 1 envelope" },
        { "failure", "{\"statusCode\":503}", "Entry is not a valid version 1 envelope" }
    };

    /// <summary>
    /// Present is not enough. A blank identity in particular must never reach a retry, where it would
    /// be rejected as an unknown identity and reported as a security event.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnusableFields))]
    public void Read_FieldThatARetryCannotUse_IsRefused(string field, string value, string reason)
    {
        var entry = WithField(DeadLetterEnvelopeJson.Serialize(Envelope(ContractFixtures.Read("valid-minimal.json"))), field, value);

        var read = DeadLetterEnvelopeJson.Read(entry);

        Assert.False(read.IsValid);
        Assert.StartsWith(reason, read.Error, StringComparison.Ordinal);
    }

    /// <summary>A later version 1 build may add a field; this one ignores it, as both contracts do.</summary>
    [Fact]
    public void Read_UnknownField_IsIgnored()
    {
        var entry = WithField(DeadLetterEnvelopeJson.Serialize(Envelope("{}")), "replayedBy", "\"operator\"");

        Assert.True(DeadLetterEnvelopeJson.Read(entry).IsValid);
    }

    /// <summary>An entry of exactly <paramref name="bytes"/> UTF-8 bytes, padded in its payload.</summary>
    private static string EntryOfSize(int bytes)
    {
        var overhead = DeadLetterEnvelopeJson.ByteCount(DeadLetterEnvelopeJson.Serialize(Envelope("")));
        var entry = DeadLetterEnvelopeJson.Serialize(Envelope(new string('p', bytes - overhead)));
        Assert.Equal(bytes, DeadLetterEnvelopeJson.ByteCount(entry));
        return entry;
    }

    private static string WithField(string entry, string field, string rawValue)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(entry)!.AsObject();
        node[field] = System.Text.Json.Nodes.JsonNode.Parse(rawValue);
        return node.ToJsonString();
    }
}
