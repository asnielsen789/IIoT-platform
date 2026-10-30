using System.Globalization;
using System.Text.Json;
using IIoT.GatewayMock.Readings;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// What the overview shows, read from the <see cref="ReadingStore"/> once per request: totals, and
/// one summary per device.
/// </summary>
/// <remarks>
/// <para>
/// Plain strings taken from the stored bodies, not typed models. The dashboard shows what the mock
/// received, as it was received; reading the bodies into the platform's types would let a bug in
/// those types change what the page says arrived, the reason the mock does not reference
/// <c>IIoT.Core</c> at all.
/// </para>
/// <para>
/// Every string here came from a request body or a token and is untrusted. The pages render them
/// through Razor, which HTML-encodes each one; nothing here is ever rendered as markup.
/// </para>
/// </remarks>
/// <param name="TakenAt">When the snapshot was taken, by the mock's clock.</param>
/// <param name="BatchCount">How many batches the mock holds.</param>
/// <param name="ReadingCount">How many readings those batches hold.</param>
/// <param name="LastStoredAt">When the newest batch was stored, or <c>null</c> before the first.</param>
/// <param name="Devices">One summary per device, ordered by device ID.</param>
public sealed record DashboardSnapshot(
    DateTimeOffset TakenAt,
    int BatchCount,
    int ReadingCount,
    DateTimeOffset? LastStoredAt,
    IReadOnlyList<DeviceSummary> Devices)
{
    public static DashboardSnapshot Create(IReadOnlyList<StoredBatch> batches, DateTimeOffset takenAt)
    {
        var views = batches.Select(BatchView.From).ToList();

        var devices = views
            .GroupBy(batch => batch.DeviceId, StringComparer.Ordinal)
            .Select(device => DeviceSummary.From(device.Key, device.ToList()))
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .ToList();

        return new DashboardSnapshot(
            takenAt,
            views.Count,
            views.Sum(batch => batch.Readings.Count),
            views.Count == 0 ? null : views.Max(batch => batch.StoredAt),
            devices);
    }

    /// <summary>
    /// The newest reading among <paramref name="batches"/>, with the batch it arrived in.
    /// </summary>
    /// <remarks>
    /// "Newest" is by the reading's own <c>at</c>, the time it was measured, not by when its batch
    /// arrived. Delivery is at least once and backfill arrives late: a redelivered or buffered batch
    /// can be stored after a newer one, and must not displace it. Ties, and readings whose <c>at</c>
    /// cannot be read as a timestamp, fall back to arrival order. The device clock can be wrong,
    /// which is why the pages show <c>at</c> next to the value.
    /// </remarks>
    internal static (ReadingView Reading, BatchView Batch)? Latest(IEnumerable<BatchView> batches)
    {
        var entries = batches
            .SelectMany(batch => batch.Readings.Select((reading, index) => (Batch: batch, Reading: reading, Index: index)))
            .ToList();

        if (entries.Count == 0)
        {
            return null;
        }

        var newest = entries.MaxBy(entry => (entry.Reading.MeasuredAt ?? DateTimeOffset.MinValue, entry.Batch.Sequence, entry.Index));
        return (newest.Reading, newest.Batch);
    }
}

/// <summary>One device as the overview shows it.</summary>
/// <param name="DeviceId">The authenticated device ID.</param>
/// <param name="Latest">Its newest reading by measurement time, of any series, or <c>null</c> if no batch holds one.</param>
/// <param name="LatestBatch">The batch <paramref name="Latest"/> arrived in.</param>
/// <param name="LastArrived">The device's most recently stored batch, which is not always the one holding its newest reading.</param>
/// <param name="MessageCount">How many batches the mock holds from the device.</param>
/// <param name="Series">The newest reading of each of its series, in <see cref="SeriesKey"/> order.</param>
public sealed record DeviceSummary(
    string DeviceId,
    ReadingView? Latest,
    BatchView? LatestBatch,
    BatchView LastArrived,
    int MessageCount,
    IReadOnlyList<SeriesLatest> Series)
{
    /// <summary>What kind of device this looks like, from what it measures.</summary>
    public DeviceType Type => DeviceType.Of(Series.Select(series => series.Key));

    /// <summary>The least trustworthy quality among the latest reading of each series.</summary>
    public string? WorstLatestQuality => BatchView.Worst(Series.Select(series => series.Reading.Quality));

    internal static DeviceSummary From(string deviceId, IReadOnlyList<BatchView> batches)
    {
        var latest = DashboardSnapshot.Latest(batches);

        // The same rule as Latest, per series: newest by measurement time, then by arrival.
        var series = batches
            .SelectMany(batch => batch.Readings.Select((reading, index) => (Batch: batch, Reading: reading, Index: index)))
            .GroupBy(entry => entry.Reading.Series)
            .Select(group => group.MaxBy(entry => (entry.Reading.MeasuredAt ?? DateTimeOffset.MinValue, entry.Batch.Sequence, entry.Index)))
            .Select(newest => new SeriesLatest(newest.Reading.Series, newest.Reading, newest.Batch))
            .OrderBy(entry => entry.Key)
            .ToList();

        return new DeviceSummary(
            deviceId,
            latest?.Reading,
            latest?.Batch,
            batches.MaxBy(batch => batch.Sequence)!,
            batches.Count,
            series);
    }
}

/// <summary>The newest reading of one series, with the batch it arrived in.</summary>
public sealed record SeriesLatest(SeriesKey Key, ReadingView Reading, BatchView Batch);

/// <summary>
/// What a device's page shows: its message history, a chart per kind it measures and, when one is
/// chosen, a message with its raw body.
/// </summary>
/// <param name="TakenAt">When the snapshot was taken, by the mock's clock.</param>
/// <param name="Device">The device's summary, as on the overview.</param>
/// <param name="NewestBatches">Its most recently stored batches, newest first, at most <see cref="MaxBatchesShown"/>.</param>
/// <param name="Trends">One chart per kind, in kind order, each with a line per sensor.</param>
/// <param name="Selected">The message chosen for the modal, or <c>null</c>.</param>
public sealed record DeviceSnapshot(
    DateTimeOffset TakenAt,
    DeviceSummary Device,
    IReadOnlyList<BatchView> NewestBatches,
    IReadOnlyList<Trend> Trends,
    SelectedMessage? Selected)
{
    /// <summary>
    /// The store is unbounded and the page refreshes itself; rendering every batch ever received
    /// on every refresh would grow without limit. The summary still considers all of them.
    /// </summary>
    public const int MaxBatchesShown = 100;

    /// <summary>The newest readings a chart plots, per series.</summary>
    public const int MaxTrendReadings = 100;

    /// <summary>
    /// The snapshot for <paramref name="deviceId"/>, or <c>null</c> when the mock holds nothing
    /// from it, or when <paramref name="messageId"/> names a message it does not hold from it.
    /// </summary>
    /// <param name="messageId">The message to show in the modal, or <c>null</c> for none.</param>
    public static DeviceSnapshot? Create(
        IReadOnlyList<StoredBatch> batches, string deviceId, string? messageId, DateTimeOffset takenAt)
    {
        var stored = batches.Where(batch => string.Equals(batch.DeviceId, deviceId, StringComparison.Ordinal)).ToList();
        if (stored.Count == 0)
        {
            return null;
        }

        SelectedMessage? selected = null;
        if (messageId is not null)
        {
            var batch = stored.FirstOrDefault(batch => string.Equals(batch.IdempotencyKey, messageId, StringComparison.Ordinal));
            if (batch is null)
            {
                return null;
            }

            selected = new SelectedMessage(BatchView.From(batch), JsonView.Tokens(batch.Body));
        }

        var views = stored.Select(BatchView.From).ToList();

        // Per series, its newest readings with a readable time and value, oldest first; then one
        // chart per kind. A kind's sensors share the chart and its scale, as they share the unit.
        var trends = views
            .SelectMany(batch => batch.Readings)
            .Where(reading => reading.MeasuredAt is not null && reading.Number is not null)
            .GroupBy(reading => reading.Series)
            .SelectMany(series => series.OrderByDescending(reading => reading.MeasuredAt).Take(MaxTrendReadings).Reverse())
            .GroupBy(reading => reading.Kind ?? "")
            .OrderBy(kind => DashboardKinds.Order(kind.Key))
            .ThenBy(kind => kind.Key, StringComparer.Ordinal)
            .Select(kind => Trend.Create(kind.Key, kind.ToList()))
            .ToList();

        return new DeviceSnapshot(
            takenAt,
            DeviceSummary.From(deviceId, views),
            views.OrderByDescending(batch => batch.Sequence).Take(MaxBatchesShown).ToList(),
            trends,
            selected);
    }
}

/// <summary>The message shown in the modal.</summary>
/// <param name="Batch">The batch, as the history shows it.</param>
/// <param name="Json">Its body as the mock stored it, indented and split into tokens for colouring.</param>
public sealed record SelectedMessage(BatchView Batch, IReadOnlyList<JsonToken> Json);

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
    /// <summary>
    /// The least trustworthy quality among the readings: <c>bad</c>, then <c>uncertain</c>, then
    /// <c>ok</c>; <c>null</c> when no reading has one.
    /// </summary>
    public string? WorstQuality => Worst(Readings.Select(reading => reading.Quality));

    /// <summary>The batch's distinct series, in <see cref="SeriesKey"/> order.</summary>
    public IReadOnlyList<SeriesKey> Series => Readings.Select(reading => reading.Series).Distinct().Order().ToList();

    /// <summary>The body's <c>contractVersion</c>: which version of the gateway contract delivered it.</summary>
    public int? ContractVersion { get; init; }

    internal static string? Worst(IEnumerable<string?> qualities) =>
        qualities.Where(quality => quality is not null).OrderByDescending(QualityRank).FirstOrDefault();

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
            readings)
        {
            ContractVersion = body.ValueKind == JsonValueKind.Object &&
                              body.TryGetProperty("contractVersion", out var version) &&
                              version.TryGetInt32(out var number)
                ? number
                : null
        };
    }

    internal static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>An unknown quality ranks between <c>ok</c> and <c>uncertain</c>: worth a look, not an alarm.</summary>
    private static int QualityRank(string? quality) => quality switch
    {
        "ok" => 0,
        "uncertain" => 2,
        "bad" => 3,
        _ => 1
    };
}

/// <summary>One reading, as the dashboard shows it.</summary>
/// <param name="Kind">What was measured.</param>
/// <param name="Value">The value exactly as it appeared in the JSON, so the page shows the number that was sent rather than a reformatted one.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="At">When it was measured, by the device clock, as sent.</param>
/// <param name="Quality">The device's own assessment.</param>
/// <param name="MeasuredAt"><paramref name="At"/> read as a timestamp, or <c>null</c> if it cannot be; used for ordering, the chart and display.</param>
/// <param name="Number"><paramref name="Value"/> as a number, or <c>null</c>; used only to place the reading on the chart.</param>
/// <param name="Sensor">Which of the device's sensors took it (v2, ADR-011), or <c>null</c>.</param>
public sealed record ReadingView(
    string? Kind,
    string? Value,
    string? Unit,
    string? At,
    string? Quality,
    DateTimeOffset? MeasuredAt,
    double? Number,
    string? Sensor = null)
{
    /// <summary>The series the reading belongs to: its kind and sensor together.</summary>
    public SeriesKey Series => new(Kind, Sensor);

    internal static ReadingView From(JsonElement reading)
    {
        var at = BatchView.StringProperty(reading, "at");
        var value = reading.TryGetProperty("value", out var element) && element.ValueKind == JsonValueKind.Number ? element : (JsonElement?)null;

        return new ReadingView(
            BatchView.StringProperty(reading, "kind"),
            value?.GetRawText(),
            BatchView.StringProperty(reading, "unit"),
            at,
            BatchView.StringProperty(reading, "quality"),
            DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var measuredAt) ? measuredAt : null,
            value is { } number && number.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : null,
            BatchView.StringProperty(reading, "sensor"));
    }
}
