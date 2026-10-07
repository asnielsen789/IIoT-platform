using System.Globalization;
using System.Text;
using System.Text.Json;
using IIoT.Core.Validation;
using IIoT.TestClient.Payloads;
using IIoT.TestClient.Tests.Fixtures;

namespace IIoT.TestClient.Tests.Payloads;

public class TelemetryMutatorTests
{
    private const string DeviceId = "tank-000777";

    /// <summary>A fixed "now", well after every fixture's own timestamps.</summary>
    private static readonly DateTimeOffset Now =
        new(2027, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(FixtureSource.Valid), MemberType = typeof(FixtureSource))]
    public void Mutate_RewritesTheFourFieldsThatMustChange(string fileName)
    {
        var original = Document(FixtureSource.ReadBytes(fileName));
        var mutated = Document(Mutate(fileName));

        // messageId: a fresh value, or a consumer deduplicating on it would drop the resend.
        Assert.Equal("fresh-message-id-1", mutated.RootElement.GetProperty("messageId").GetString());
        Assert.NotEqual(
            original.RootElement.GetProperty("messageId").GetString(),
            mutated.RootElement.GetProperty("messageId").GetString());

        // sentAt: now.
        Assert.Equal(Now, Instant(mutated.RootElement.GetProperty("sentAt")));

        // deviceId: the authenticated identity. Two fixtures hard-code tank-000123, and the
        // consumer rejects a mismatch against iothub-connection-device-id as a security event, so
        // without this rewrite every message from any other device is correctly refused.
        Assert.Equal(DeviceId, mutated.RootElement.GetProperty("deviceId").GetString());

        // measuredAt: every one of them moved.
        var originalMeasuredAt = MeasuredAt(original);
        var mutatedMeasuredAt = MeasuredAt(mutated);
        Assert.Equal(originalMeasuredAt.Count, mutatedMeasuredAt.Count);
        Assert.All(
            originalMeasuredAt.Zip(mutatedMeasuredAt),
            pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void Mutate_PreservesTheShapeOfBufferedBackfill()
    {
        const string FileName = "valid-buffered-backfill.json";

        var original = Document(FixtureSource.ReadBytes(FileName));
        var mutated = Document(Mutate(FileName));

        var before = MeasuredAt(original);
        var after = MeasuredAt(mutated);

        // Three readings, hours apart, deliberately older than sentAt: this fixture is the one
        // that exercises a device emptying its buffer after an outage.
        Assert.Equal(3, before.Count);
        Assert.Equal(3, after.Count);

        // Relative spacing survives exactly. Re-stamping each reading as "now" independently would
        // flatten it into three simultaneous readings and the fixture would stop testing anything.
        Assert.Equal(before[1] - before[0], after[1] - after[0]);
        Assert.Equal(before[2] - before[1], after[2] - after[1]);

        // And they stay backfill: still older than sentAt, by the original margin.
        var sentAt = Instant(mutated.RootElement.GetProperty("sentAt"));
        var originalSentAt = Instant(original.RootElement.GetProperty("sentAt"));
        Assert.All(after, measuredAt => Assert.True(
            measuredAt < sentAt,
            $"measuredAt {measuredAt:O} is not older than sentAt {sentAt:O}"));
        Assert.Equal(originalSentAt - before[^1], sentAt - after[^1]);
        Assert.True(sentAt - after[0] > TimeSpan.FromHours(8));
    }

    /// <summary>
    /// The test that proves the client and the Azure Function agree about the contract. The client
    /// never references <c>IIoT.Core</c>; this test does, and runs the mutated bytes through the
    /// very validator the Function uses. If the mutation drifted away from the schema — a bad
    /// timestamp format, a dropped required field — it would fail here rather than at 03:00 against
    /// a live hub.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureSource.Valid), MemberType = typeof(FixtureSource))]
    public void Mutate_LeavesEveryValidFixtureValid(string fileName)
    {
        var result = TelemetryValidator.Validate(Encoding.UTF8.GetString(Mutate(fileName)));

        Assert.True(result.IsValid, $"{fileName} failed validation after mutation: {result.Error}");
        Assert.Equal(DeviceId, result.Message!.DeviceId);
    }

    /// <summary>
    /// Mutation does not launder an invalid payload into a valid one. It only touches identity and
    /// timing; the reason each invalid fixture is invalid lies elsewhere, and must survive.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureSource.Invalid), MemberType = typeof(FixtureSource))]
    public void Mutate_LeavesEveryInvalidFixtureInvalid(string fileName)
    {
        var result = TelemetryValidator.Validate(Encoding.UTF8.GetString(Mutate(fileName)));

        Assert.False(result.IsValid, $"{fileName} became valid after mutation");
    }

    [Fact]
    public void Mutate_PreservesUnknownFields()
    {
        var mutated = Document(Mutate("valid-unknown-fields.json"));

        // Section 6 of the contract: unknown fields must be ignored, not rejected. The mutator
        // edits the JSON tree rather than round-tripping through typed models precisely so these
        // survive and the fixture keeps testing forward compatibility.
        Assert.Equal(
            20481,
            mutated.RootElement.GetProperty("measurements")[0].GetProperty("rawAdcValue").GetInt32());
        Assert.Equal(
            "must be ignored, not rejected",
            mutated.RootElement.GetProperty("device").GetProperty("newVendorField").GetString());
    }

    [Fact]
    public void Mutate_GivesEachMessageItsOwnMessageId()
    {
        var counter = 0;
        var mutator = new TelemetryMutator(
            () => Now,
            () => $"fresh-message-id-{++counter}");

        var payload = FixtureSource.ReadBytes("valid-minimal.json");
        var first = Document(mutator.Mutate(payload, DeviceId));
        var second = Document(mutator.Mutate(payload, DeviceId));

        Assert.NotEqual(
            first.RootElement.GetProperty("messageId").GetString(),
            second.RootElement.GetProperty("messageId").GetString());
    }

    [Fact]
    public void MessageId_FitsTheContractsLengthLimit()
    {
        // The schema caps messageId at 64 characters.
        Assert.InRange(MessageId.New(Now).Length, 1, 64);
        Assert.NotEqual(MessageId.New(Now), MessageId.New(Now));
    }

    private static byte[] Mutate(string fileName)
    {
        var counter = 0;
        var mutator = new TelemetryMutator(() => Now, () => $"fresh-message-id-{++counter}");

        return mutator.Mutate(FixtureSource.ReadBytes(fileName), DeviceId);
    }

    private static JsonDocument Document(byte[] payload) => JsonDocument.Parse(payload);

    private static List<DateTimeOffset> MeasuredAt(JsonDocument document) =>
        document.RootElement.GetProperty("measurements")
            .EnumerateArray()
            .Select(measurement => Instant(measurement.GetProperty("measuredAt")))
            .ToList();

    private static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.Parse(
            element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
