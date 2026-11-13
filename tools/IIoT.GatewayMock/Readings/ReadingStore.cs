using System.Collections.Concurrent;
using System.Text.Json;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// The batches the mock has accepted, in memory. Public so that in-process tests can see exactly
/// what arrived; the dashboard, when it is enabled, shows the same batches to people.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by device <em>and</em> idempotency key. The device contract only promises that a
/// <c>messageId</c> is unique per message from one device, not across the fleet. A receiver that
/// keyed on the message id alone would drop one device's data as a "duplicate" of another's, and
/// a device could suppress another device's readings by reusing its message ids. Scoped to the
/// authenticated device id, a device can only ever collide with itself.
/// </para>
/// <para>
/// Unbounded, because the mock is a development tool that never ships. A real receiver would
/// expire keys after its replay window. With the archive configured (ADR-012), it bounds what a
/// restart brings back: its retention rule deletes older batches.
/// </para>
/// <para>
/// Memory only. <see cref="ReadingHistory"/> puts the archive in front of it; the endpoint stores
/// through that, and tests and the dashboard read from here.
/// </para>
/// </remarks>
public sealed class ReadingStore
{
    private readonly ConcurrentDictionary<(string DeviceId, string IdempotencyKey), StoredBatch> _batches = new();
    private long _sequence;
    private long _restoredSequence = 1;

    /// <summary>Every stored batch, in the order it was accepted.</summary>
    public IReadOnlyList<StoredBatch> Batches =>
        _batches.Values.OrderBy(batch => batch.Sequence).ToList();

    /// <summary>
    /// Stores <paramref name="body"/> unless this device has already delivered this key.
    /// </summary>
    /// <returns><c>true</c> if stored; <c>false</c> if it was a replay and nothing was stored.</returns>
    public bool TryAdd(string deviceId, string idempotencyKey, JsonElement body, string? caller, DateTimeOffset storedAt)
    {
        var batch = new StoredBatch(
            Interlocked.Increment(ref _sequence), deviceId, idempotencyKey, caller, storedAt, body.Clone());

        return _batches.TryAdd((deviceId, idempotencyKey), batch);
    }

    /// <summary>
    /// Adds a batch accepted before this process started, read back from the archive. It is placed
    /// in front of every batch this process accepted itself, and of every batch restored before it:
    /// restore the newest first. Restored batches have sequence numbers of zero and below.
    /// </summary>
    /// <returns><c>true</c> if added; <c>false</c> if memory already held it.</returns>
    public bool TryRestore(string deviceId, string idempotencyKey, JsonElement body, string? caller, DateTimeOffset storedAt)
    {
        var batch = new StoredBatch(
            Interlocked.Decrement(ref _restoredSequence), deviceId, idempotencyKey, caller, storedAt, body.Clone());

        return _batches.TryAdd((deviceId, idempotencyKey), batch);
    }
}

/// <summary>One accepted batch, as the gateway received it.</summary>
/// <param name="Sequence">
/// Arrival order. Batches read back from the archive after a restart come first, with numbers of
/// zero and below.
/// </param>
/// <param name="DeviceId">The batch's <c>deviceId</c>.</param>
/// <param name="IdempotencyKey">The request's <c>Idempotency-Key</c> header.</param>
/// <param name="Caller">The token's <c>sub</c> claim: which client delivered it.</param>
/// <param name="StoredAt">When the mock accepted it.</param>
/// <param name="Body">The request body exactly as received, as JSON rather than typed models.</param>
public sealed record StoredBatch(
    long Sequence,
    string DeviceId,
    string IdempotencyKey,
    string? Caller,
    DateTimeOffset StoredAt,
    JsonElement Body);
