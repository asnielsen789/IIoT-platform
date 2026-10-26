using System.Globalization;
using System.Text.Json;
using IIoT.GatewayMock.Readings;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// What one view of the dashboard shows, read from the <see cref="ReadingStore"/> once per request.
/// </summary>
/// <remarks>
/// <para>
/// Plain strings taken from the stored bodies, not typed models. The dashboard shows what the mock
/// received, as it was received; reading the bodies into the platform's types would let a bug in
/// those types change what the page says arrived, the reason the mock does not reference
/// <c>IIoT.Core</c> at all.
/// </para>
/// <para>
/// Every string here came from a request body or a token and is untrusted. The page renders them
/// through Razor, which HTML-encodes each one; nothing here is ever rendered as markup.
/// </para>
/// </remarks>
/// <param name="TakenAt">When the snapshot was taken, by the mock's clock.</param>
/// <param name="BatchCount">How many batches the mock holds.</param>
/// <param name="LatestPerDevice">The newest reading of every device, ordered by device ID.</param>
/// <param name="NewestBatches">The most recently stored batches, newest first, at most <see cref="MaxBatchesShown"/>.</param>
public sealed record DashboardSnapshot(
    DateTimeOffset TakenAt,
    int BatchCount,
    IReadOnlyList<DeviceLatestReading> LatestPerDevice,
    IReadOnlyList<BatchView> NewestBatches)
{
    /// <summary>
    /// The store is unbounded and the page refreshes itself; rendering every batch ever received on
    /// every refresh would grow without limit. The latest reading per device still considers all.
    /// </summary>
    public const int MaxBatchesShown = 100;

    public static DashboardSnapshot Create(IReadOnlyList<StoredBatch> batches, DateTimeOffset takenAt)
    {
        var views = batches.Select(BatchView.From).ToList();

        var latest = views
            .SelectMany(batch => batch.Readings.Select((reading, index) => (Batch: batch, Reading: reading, Index: index)))
            .GroupBy(entry => entry.Batch.DeviceId, StringComparer.Ordinal)
            .Select(device => device.MaxBy(entry => (entry.Reading.MeasuredAt ?? DateTimeOffset.MinValue, entry.Batch.Sequence, entry.Index)))
            .OrderBy(entry => entry.Batch.DeviceId, StringComparer.Ordinal)
            .Select(entry => new DeviceLatestReading(entry.Batch.DeviceId, entry.Reading, entry.Batch))
            .ToList();

        var newest = views
            .OrderByDescending(batch => batch.Sequence)
            .Take(MaxBatchesShown)
            .ToList();

        return new DashboardSnapshot(takenAt, views.Count, latest, newest);
    }
}

/// <summary>
/// The newest reading of one device.
/// </summary>
/// <remarks>
/// "Newest" is by the reading's own <c>at</c>, the time it was measured, not by when its batch
/// arrived. Delivery is at least once and backfill arrives late: a redelivered or buffered batch
/// can be stored after a newer one, and must not displace it. Ties, and readings whose <c>at</c>
/// cannot be read as a timestamp, fall back to arrival order. The device clock can be wrong, which
/// is why the page shows <c>at</c> next to the value.
/// </remarks>
/// <param name="DeviceId">The authenticated device ID.</param>
/// <param name="Reading">Its newest reading.</param>
/// <param name="Batch">The batch that reading arrived in.</param>
public sealed record DeviceLatestReading(string DeviceId, ReadingView Reading, BatchView Batch);

/// <summary>One stored batch, as the dashboard shows it.</summary>
/// <param name="Sequence">Arrival order in the mock.</param>
/// <param name="StoredAt">When the mock stored it, by the mock's clock.</param>
/// <param name="ReceivedAt">The batch's <c>receivedAt</c>: when the platform received the message, by the platform's clock.</param>
/// <param name="DeviceId">The batch's <c>deviceId</c>.</param>
/// <param name="MessageId">The message ID, which is also the request's <c>Idempotency-Key</c>; the mock refuses a request in which they differ.</param>
/// <param name="Caller">The <c>sub</c> claim of the token that delivered it: which client sent it.</param>
/// <param name="Firmware">The firmware the device reported.</param>
/// <param name="Readings">Its readings, in the order the device sent them.</param>
public sealed record BatchView(
    long Sequence,
    DateTimeOffset StoredAt,
    string? ReceivedAt,
    string DeviceId,
    string MessageId,
    string? Caller,
    string? Firmware,
    IReadOnlyList<ReadingView> Readings)
{
    /// <remarks>
    /// Every body in the store passed the schema, so each field is present and of its type. The
    /// store is public, though, and the page must not fail on a body that was put there some other
    /// way: a field that is missing or of another type is shown as absent.
    /// </remarks>
    internal static BatchView From(StoredBatch batch)
    {
        var body = batch.Body;

        var readings = body.ValueKind == JsonValueKind.Object &&
                       body.TryGetProperty("readings", out var array) &&
                       array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(reading => reading.ValueKind == JsonValueKind.Object).Select(ReadingView.From).ToList()
            : [];

        return new BatchView(
            batch.Sequence,
            batch.StoredAt,
            StringProperty(body, "receivedAt"),
            batch.DeviceId,
            batch.IdempotencyKey,
            batch.Caller,
            StringProperty(body, "firmware"),
            readings);
    }

    internal static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One reading, as the dashboard shows it.</summary>
/// <param name="Kind">What was measured.</param>
/// <param name="Value">The value exactly as it appeared in the JSON, so the page shows the number that was sent rather than a reformatted one.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="At">When it was measured, by the device clock, as sent.</param>
/// <param name="Quality">The device's own assessment.</param>
/// <param name="MeasuredAt"><paramref name="At"/> read as a timestamp, or <c>null</c> if it cannot be; used only for ordering.</param>
public sealed record ReadingView(
    string? Kind,
    string? Value,
    string? Unit,
    string? At,
    string? Quality,
    DateTimeOffset? MeasuredAt)
{
    internal static ReadingView From(JsonElement reading)
    {
        var at = BatchView.StringProperty(reading, "at");

        return new ReadingView(
            BatchView.StringProperty(reading, "kind"),
            reading.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null,
            BatchView.StringProperty(reading, "unit"),
            at,
            BatchView.StringProperty(reading, "quality"),
            DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var measuredAt) ? measuredAt : null);
    }
}
