namespace IIoT.TestClient.Payloads;

/// <summary>
/// One of the contract's example payloads, held as the exact bytes found on disk.
/// </summary>
/// <param name="Name">The file name, e.g. <c>valid-buffered-backfill.json</c>.</param>
/// <param name="Payload">The file's bytes, unaltered.</param>
/// <remarks>
/// Bytes, not a deserialised model. The client must not reference <c>IIoT.Core</c>: if it
/// round-tripped payloads through the platform's own models, a bug in those models would be
/// invisible, because the client and the Function would agree with each other while both
/// disagreed with the published contract. The fixture files are the single arbiter.
/// </remarks>
public sealed record TelemetryFixture(string Name, byte[] Payload)
{
    /// <summary>True for a payload the pipeline is expected to accept.</summary>
    public bool IsExpectedValid =>
        Name.StartsWith(TelemetryFixtures.ValidPrefix, StringComparison.Ordinal);
}
