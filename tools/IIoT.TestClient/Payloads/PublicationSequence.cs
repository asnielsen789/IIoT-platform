using IIoT.TestClient.IotHub;

namespace IIoT.TestClient.Payloads;

/// <summary>
/// Turns a selection of fixtures into the endless sequence of messages one device sends.
/// </summary>
/// <remarks>
/// Deliberately knows nothing about MQTT. Everything that decides what reaches the hub — the topic
/// and the bytes — is decided here, so it can all be asserted offline. That matters because there
/// is no IoT Hub to test against yet.
/// </remarks>
public sealed class PublicationSequence
{
    private readonly IReadOnlyList<TelemetryFixture> _fixtures;
    private readonly TelemetryMutator? _mutator;

    /// <param name="fixtures">The selected fixtures, cycled in order.</param>
    /// <param name="mutator">
    /// The mutator, or <see langword="null"/> for <c>--no-mutate</c>. When null the fixture bytes
    /// are sent verbatim, which is what makes the client usable for contract and negative testing:
    /// the hub receives precisely the bytes in <c>docs/schemas/examples/</c>.
    /// </param>
    public PublicationSequence(IReadOnlyList<TelemetryFixture> fixtures, TelemetryMutator? mutator)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        if (fixtures.Count == 0)
            throw new ArgumentException("At least one fixture is required.", nameof(fixtures));

        _fixtures = fixtures;
        _mutator = mutator;
    }

    /// <summary>How many fixtures are in the rotation.</summary>
    public int Count => _fixtures.Count;

    /// <summary>Builds message number <paramref name="index"/> for <paramref name="deviceId"/>.</summary>
    public TelemetryPublication Next(string deviceId, long index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var fixture = _fixtures[(int)(index % _fixtures.Count)];
        var payload = _mutator is null
            ? fixture.Payload
            : _mutator.Mutate(fixture.Payload, deviceId);

        return new TelemetryPublication(fixture.Name, DeviceTopic.Events(deviceId), payload);
    }
}
