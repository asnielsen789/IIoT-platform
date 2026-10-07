using IIoT.TestClient.Payloads;
using IIoT.TestClient.Tests.Fixtures;

namespace IIoT.TestClient.Tests.Payloads;

public class PublicationSequenceTests
{
    private const string DeviceId = "tank-000777";

    [Theory]
    [MemberData(nameof(FixtureSource.Valid), MemberType = typeof(FixtureSource))]
    public void NoMutate_SendsTheFileBytesVerbatim(string fileName)
    {
        var sequence = new PublicationSequence(FixtureSource.Store.Select(fileName), mutator: null);

        var publication = sequence.Next(DeviceId, 0);

        // Byte-identical to the file on disk. This is what makes --no-mutate usable for contract
        // testing: the hub receives exactly the bytes published in docs/schemas/examples/, with no
        // chance that a reserialisation step quietly normalised something.
        Assert.Equal(FixtureSource.ReadBytes(fileName), publication.Payload);
    }

    [Theory]
    [MemberData(nameof(FixtureSource.Invalid), MemberType = typeof(FixtureSource))]
    public void NoMutate_SendsInvalidFixturesVerbatimToo(string fileName)
    {
        var sequence = new PublicationSequence(FixtureSource.Store.Select(fileName), mutator: null);

        Assert.Equal(FixtureSource.ReadBytes(fileName), sequence.Next(DeviceId, 0).Payload);
    }

    [Fact]
    public void Next_AlwaysUsesTheContractTopic()
    {
        var sequence = new PublicationSequence(
            FixtureSource.Store.Select(TelemetryFixtures.AllSelector), mutator: null);

        for (long index = 0; index < sequence.Count; index++)
        {
            Assert.Equal(
                "devices/tank-000777/messages/events/$.ct=application%2Fjson&$.ce=utf-8",
                sequence.Next(DeviceId, index).Topic);
        }
    }

    [Fact]
    public void Next_CyclesThroughTheSelection()
    {
        var selected = FixtureSource.Store.Select(TelemetryFixtures.ValidSelector);
        var sequence = new PublicationSequence(selected, mutator: null);

        var names = Enumerable.Range(0, selected.Count * 2)
            .Select(i => sequence.Next(DeviceId, i).FixtureName)
            .ToList();

        Assert.Equal(selected.Select(f => f.Name), names.Take(selected.Count));
        Assert.Equal(names.Take(selected.Count), names.Skip(selected.Count));
    }

    [Fact]
    public void Next_WithAMutator_StampsTheDeviceIdOfTheCaller()
    {
        var sequence = new PublicationSequence(
            FixtureSource.Store.Select("valid-full.json"),
            new TelemetryMutator(() => DateTimeOffset.UnixEpoch, () => "fixed"));

        var payload = System.Text.Encoding.UTF8.GetString(sequence.Next(DeviceId, 0).Payload);

        Assert.Contains($"\"deviceId\":\"{DeviceId}\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("tank-000123", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsAnEmptySelection() =>
        Assert.Throws<ArgumentException>(() => new PublicationSequence([], mutator: null));
}
