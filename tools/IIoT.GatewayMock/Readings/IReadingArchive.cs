namespace IIoT.GatewayMock.Readings;

/// <summary>
/// Where accepted batches outlive the process (ADR-012): the request bodies exactly as they
/// arrived, one entry per device and <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// The archive, not memory, decides what is a replay when it is configured: it is the one record
/// that survives a restart, and that every instance of the mock shares. See
/// <see cref="ReadingHistory"/>.
/// </remarks>
public interface IReadingArchive
{
    /// <summary>
    /// Writes <paramref name="batch"/> unless this device has already delivered this key. Never
    /// overwrites: the first delivery wins.
    /// </summary>
    /// <returns><c>true</c> if written; <c>false</c> if the archive already held it.</returns>
    /// <exception cref="Exception">The archive could not be reached or refused the write.</exception>
    Task<bool> TryAddAsync(ArchivedBatch batch, CancellationToken cancellationToken);

    /// <summary>Every batch the archive holds, in no particular order.</summary>
    Task<IReadOnlyList<ArchivedBatch>> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>One accepted batch, as the archive keeps it.</summary>
/// <param name="DeviceId">The batch's <c>deviceId</c>.</param>
/// <param name="IdempotencyKey">The request's <c>Idempotency-Key</c> header.</param>
/// <param name="Caller">The token's <c>sub</c> claim, if it had one.</param>
/// <param name="StoredAt">When the mock accepted it.</param>
/// <param name="Body">The request body, byte for byte.</param>
public sealed record ArchivedBatch(
    string DeviceId,
    string IdempotencyKey,
    string? Caller,
    DateTimeOffset StoredAt,
    ReadOnlyMemory<byte> Body);
