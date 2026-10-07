using System.Globalization;

namespace IIoT.TestClient.Payloads;

/// <summary>Generates the per-message identifier the mutator writes into a replayed payload.</summary>
/// <remarks>
/// Real firmware is expected to emit a ULID. The test client only needs the two properties the
/// contract actually requires — unique, and at most 64 characters — so it uses a plainly synthetic
/// form instead. A leading timestamp keeps a run's messages sortable in a log; the GUID carries
/// the uniqueness.
/// </remarks>
public static class MessageId
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss";

    /// <summary>Creates an identifier stamped with <paramref name="now"/>.</summary>
    public static string New(DateTimeOffset now) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{now.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");

    /// <summary>Creates an identifier stamped with the current time.</summary>
    public static string New() => New(DateTimeOffset.UtcNow);
}
