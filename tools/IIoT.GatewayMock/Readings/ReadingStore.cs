using System.Collections.Concurrent;
using System.Text.Json;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// The batches the mock has accepted, in memory. Public so that in-process tests can see exactly
/// what arrived without the mock needing an inspection endpoint.
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
/// expire keys after its replay window.
/// </para>
/// </remarks>
public sealed class ReadingStore
{
    private readonly ConcurrentDictionary<(string DeviceId, string IdempotencyKey), StoredBatch> _batches = new();
    private long _sequence;

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
}

/// <summary>One accepted batch, as the gateway received it.</summary>
/// <param name="Sequence">Arrival order.</param>
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
