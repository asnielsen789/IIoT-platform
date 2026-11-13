using System.Text.Json;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// What the mock has accepted: the in-memory <see cref="ReadingStore"/>, backed by the
/// <see cref="IReadingArchive"/> when one is configured (ADR-012).
/// </summary>
/// <remarks>
/// <para>
/// A delivery is written to the archive first, and only then to memory. The archive decides what is
/// a replay, since it is the record that survives a restart; memory is what the dashboard and the
/// tests read. A delivery the archive cannot take is not accepted, so the sender keeps it and tries
/// again, as it would with any receiver that cannot store.
/// </para>
/// <para>
/// After a restart memory is empty. It is filled again from the archive the first time someone
/// needs the history, which is when the dashboard is opened, not at startup: the hosted mock
/// starts again after every idle spell, mostly to take one delivery, and reading the archive back
/// each time would cost a read per stored batch for nothing. Until then, deliveries are archived and
/// acknowledged as usual.
/// </para>
/// </remarks>
public sealed class ReadingHistory
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly ReadingStore _store;
    private readonly IReadingArchive? _archive;
    private readonly ILogger<ReadingHistory> _logger;
    private readonly SemaphoreSlim _restoring = new(1, 1);
    private volatile bool _restored;
    private volatile bool _restoreFailed;

    public ReadingHistory(ReadingStore store, IReadingArchive? archive, ILogger<ReadingHistory> logger)
    {
        _store = store;
        _archive = archive;
        _logger = logger;
    }

    /// <summary>Whether accepted batches are archived, and so outlive the process.</summary>
    public bool IsArchived => _archive is not null;

    /// <summary>
    /// Whether the last attempt to read the archive back failed, so that memory holds only what
    /// arrived since the mock started. The dashboard says so; the next request tries again.
    /// </summary>
    public bool RestoreFailed => _restoreFailed;

    /// <summary>Stores a validated batch, unless this device has already delivered this key.</summary>
    /// <param name="body">The request body, byte for byte: what the archive keeps.</param>
    /// <param name="parsed">The same body, parsed: what memory keeps.</param>
    /// <returns><c>true</c> if stored; <c>false</c> if it was a replay and nothing was stored.</returns>
    /// <exception cref="Exception">The archive could not take the batch; nothing was stored.</exception>
    public async Task<bool> TryAddAsync(
        string deviceId,
        string idempotencyKey,
        ReadOnlyMemory<byte> body,
        JsonElement parsed,
        string? caller,
        DateTimeOffset storedAt,
        CancellationToken cancellationToken)
    {
        if (_archive is null)
        {
            return _store.TryAdd(deviceId, idempotencyKey, parsed, caller, storedAt);
        }

        if (!await _archive.TryAddAsync(new ArchivedBatch(deviceId, idempotencyKey, caller, storedAt, body), cancellationToken))
        {
            return false;
        }

        // Archived, so accepted, whatever memory says: memory can hold the batch already only if a
        // restore read it back between the two writes.
        _store.TryAdd(deviceId, idempotencyKey, parsed, caller, storedAt);
        return true;
    }

    /// <summary>
    /// Fills memory from the archive, once per process. Without an archive, or once done, it
    /// returns at once. A failure is logged, not thrown: the caller shows what memory holds, and
    /// the next call tries again.
    /// </summary>
    public async Task EnsureRestoredAsync(CancellationToken cancellationToken)
    {
        if (_archive is null || _restored)
        {
            return;
        }

        await _restoring.WaitAsync(cancellationToken);
        try
        {
            if (_restored)
            {
                return;
            }

            var archived = await _archive.ReadAllAsync(cancellationToken);
            var restored = 0;

            // Newest first: each restored batch goes in front of the one before it, so the oldest
            // ends up first, and all of them before what this process accepted itself.
            foreach (var batch in archived.OrderByDescending(batch => batch.StoredAt))
            {
                JsonDocument document;
                try
                {
                    document = Parse(batch.Body);
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Skipped an archived batch whose content is not JSON");
                    continue;
                }

                using (document)
                {
                    if (_store.TryRestore(batch.DeviceId, batch.IdempotencyKey, document.RootElement, batch.Caller, batch.StoredAt))
                    {
                        restored++;
                    }
                }
            }

            _restored = true;
            _restoreFailed = false;
            _logger.LogInformation("Restored {Count} batch(es) from the archive", restored);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _restoreFailed = true;
            _logger.LogError(ex, "Could not read the archive back; showing only what arrived since the mock started");
        }
        finally
        {
            _restoring.Release();
        }
    }

    /// <summary>
    /// Parses a request body from its bytes. A stream parser skips a UTF-8 byte order mark, and the
    /// endpoint once parsed the request stream; a byte parser does not, so it is skipped here. The
    /// document refers to <paramref name="body"/> rather than copying it.
    /// </summary>
    /// <exception cref="JsonException">Not JSON.</exception>
    internal static JsonDocument Parse(ReadOnlyMemory<byte> body) =>
        JsonDocument.Parse(body.Span.StartsWith(Utf8Bom) ? body[Utf8Bom.Length..] : body);
}
