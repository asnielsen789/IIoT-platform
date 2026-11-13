using System.Collections.Concurrent;
using IIoT.GatewayMock.Readings;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// Stands in for the mock's archive. Outlives the mock it is given to, so a second mock built with
/// the same archive is the first one after a restart; and fails on request, as storage does when it
/// is unreachable or refuses the app's identity.
/// </summary>
public sealed class InMemoryReadingArchive : IReadingArchive
{
    private readonly ConcurrentDictionary<(string DeviceId, string IdempotencyKey), ArchivedBatch> _batches = new();
    private int _reads;

    /// <summary>Thrown by every write while set.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>Thrown by every read while set.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>How often the whole archive has been read.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public IReadOnlyList<ArchivedBatch> Batches => [.. _batches.Values];

    /// <summary>Puts a batch in the archive directly, as an earlier run of the mock would have.</summary>
    public void Add(ArchivedBatch batch) => _batches[(batch.DeviceId, batch.IdempotencyKey)] = batch;

    public Task<bool> TryAddAsync(ArchivedBatch batch, CancellationToken cancellationToken)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        // A copy, as storage would keep: the caller's buffer is not the archive's.
        var copy = batch with { Body = batch.Body.ToArray() };
        return Task.FromResult(_batches.TryAdd((batch.DeviceId, batch.IdempotencyKey), copy));
    }

    public Task<IReadOnlyList<ArchivedBatch>> ReadAllAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);

        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(Batches);
    }
}
