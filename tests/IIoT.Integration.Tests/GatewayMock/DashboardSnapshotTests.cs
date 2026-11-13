using System.Text.Json;
using System.Text.Json.Nodes;
using IIoT.GatewayMock.Dashboard;
using IIoT.GatewayMock.Readings;
using IIoT.Integration.Tests.Infrastructure;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// What the dashboard reads out of the store: the newest reading per device, a device's history
/// and level trend, and a message's body. Tested on the snapshots directly, where the rules are
/// visible, rather than by picking values out of the rendered pages.
/// </summary>
public class DashboardSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Backfill arrives late: a batch of readings buffered through an outage can be stored after a
    /// live batch with a newer measurement. The newest measurement is still the latest reading.
    /// </summary>
    [Fact]
    public void LatestReading_IsTheNewestMeasurement_NotTheLastArrival()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "live", ("1500", "2026-09-14T06:00:00+00:00"));
        Add(store, "tank-000001", "backfill", ("1400", "2026-09-13T18:00:00+00:00"), ("1450", "2026-09-13T22:00:00+00:00"));

        var device = Assert.Single(DashboardSnapshot.Create(store.Batches, Now).Devices);

        Assert.Equal("1500", device.Latest!.Value);
        Assert.Equal("live", device.LatestBatch!.MessageId);

        // The backfill is still what arrived last.
        Assert.Equal("backfill", device.LastArrived.MessageId);
        Assert.Equal(2, device.MessageCount);
    }

    /// <summary>
    /// Offsets are honoured: 07:30+02:00 is earlier than 06:00Z, whatever the wall-clock digits say.
    /// </summary>
    [Fact]
    public void LatestReading_ComparesInstants_NotLocalTimes()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "utc", ("1", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000001", "offset", ("2", "2026-09-14T07:30:00+02:00"));

        var device = Assert.Single(DashboardSnapshot.Create(store.Batches, Now).Devices);

        Assert.Equal("1", device.Latest!.Value);
    }

    [Fact]
    public void LatestReading_IsPerDevice_OrderedByDeviceId()
    {
        var store = new ReadingStore();
        Add(store, "tank-000002", "b", ("2", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000001", "a", ("1", "2026-09-14T06:00:00Z"));

        var snapshot = DashboardSnapshot.Create(store.Batches, Now);

        Assert.Equal(["tank-000001", "tank-000002"], snapshot.Devices.Select(device => device.DeviceId));
        Assert.Equal(2, snapshot.BatchCount);
        Assert.Equal(2, snapshot.ReadingCount);
        Assert.Equal(Now, snapshot.LastStoredAt);
    }

    [Fact]
    public void EmptyStore_HasNoDevices_AndNoLastMessage()
    {
        var snapshot = DashboardSnapshot.Create(new ReadingStore().Batches, Now);

        Assert.Empty(snapshot.Devices);
        Assert.Equal(0, snapshot.BatchCount);
        Assert.Null(snapshot.LastStoredAt);
    }

    [Fact]
    public void Device_UnknownDeviceOrMessage_IsNull()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "m1", ("1", "2026-09-14T06:00:00Z"));

        Assert.Null(DeviceSnapshot.Create(store.Batches, "tank-000002", null, Now));
        Assert.Null(DeviceSnapshot.Create(store.Batches, "tank-000001", "no-such-message", Now));

        // Message IDs are unique per device, not globally: another device's message is not this one's.
        Add(store, "tank-000002", "m2", ("2", "2026-09-14T06:00:00Z"));
        Assert.Null(DeviceSnapshot.Create(store.Batches, "tank-000001", "m2", Now));
    }

    [Fact]
    public void Device_HasOnlyItsOwnBatches_AndTheChosenMessage()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "a", ("1", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000002", "b", ("2", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000001", "c", ("3", "2026-09-14T10:00:00Z"));

        var snapshot = DeviceSnapshot.Create(store.Batches, "tank-000001", "a", Now)!;

        Assert.Equal(["c", "a"], snapshot.NewestBatches.Select(batch => batch.MessageId));
        Assert.Equal("3", snapshot.Device.Latest!.Value);
        Assert.Equal("a", snapshot.Selected!.Batch.MessageId);
        Assert.Contains(snapshot.Selected.Json, token => token is { Kind: "key", Text: "\"messageId\"" });
        Assert.Contains(snapshot.Selected.Json, token => token is { Kind: "string", Text: "\"a\"" });
    }

    /// <summary>
    /// The trend runs by measurement time, so a backfill that arrived last is drawn before the live
    /// reading it predates, and the chart keeps only the newest readings.
    /// </summary>
    [Fact]
    public void Trend_IsInMeasurementOrder_AndCapped()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "live", ("1500", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000001", "backfill", ("1400", "2026-09-13T18:00:00Z"), ("1450", "2026-09-13T22:00:00Z"));

        var points = Assert.Single(Assert.Single(DeviceSnapshot.Create(store.Batches, "tank-000001", null, Now)!.Trends).Lines).Points;

        Assert.Equal(["1400", "1450", "1500"], points.Select(point => point.Reading.Value));
        Assert.True(points[0].X < points[1].X && points[1].X < points[2].X);

        var many = new ReadingStore();
        for (var i = 0; i < DeviceSnapshot.MaxTrendReadings + 10; i++)
        {
            Add(many, "tank-000001", $"m{i:D3}", ($"{i}", $"2026-09-14T06:{i / 60:D2}:{i % 60:D2}Z"));
        }

        var capped = Assert.Single(Assert.Single(DeviceSnapshot.Create(many.Batches, "tank-000001", null, Now)!.Trends).Lines).Points;
        Assert.Equal(DeviceSnapshot.MaxTrendReadings, capped.Count);
        Assert.Equal("10", capped[0].Reading.Value);
    }

    /// <summary>
    /// A car wash delivered on v2 (ADR-011): five series, each with its latest reading, a chart per
    /// kind with a line per sensor, and the type it looks like.
    /// </summary>
    [Fact]
    public void Device_OfSeveralSeries_HasEachSeries_AndAChartPerKind()
    {
        var store = new ReadingStore();
        using (var document = JsonDocument.Parse(ContractFixtures.ReadGatewayV2("valid-car-wash.json")))
        {
            store.TryAdd("wash-000123", "m1", document.RootElement, caller: null, Now);
        }

        var snapshot = DeviceSnapshot.Create(store.Batches, "wash-000123", "m1", Now)!;

        Assert.Equal(DeviceType.CarWash, snapshot.Device.Type);
        Assert.Equal(
            ["Level · reclaim", "Level · shampoo", "Level · wax", "Volume · water", "Count · cycles"],
            snapshot.Device.Series.Select(series => series.Key.Label));
        Assert.Equal("uncertain", snapshot.Device.WorstLatestQuality);
        Assert.Equal(["level", "volume", "count"], snapshot.Trends.Select(trend => trend.Kind));
        Assert.Equal(["reclaim", "shampoo", "wax"], snapshot.Trends[0].Lines.Select(line => line.Sensor));
        Assert.Equal(2, snapshot.Selected!.Batch.ContractVersion);
        Assert.Equal(5, snapshot.NewestBatches[0].Series.Count);
    }

    [Theory]
    [InlineData(new[] { "ok", "ok" }, "ok")]
    [InlineData(new[] { "ok", "uncertain" }, "uncertain")]
    [InlineData(new[] { "uncertain", "bad", "ok" }, "bad")]
    public void WorstQuality_IsTheLeastTrustworthy(string[] qualities, string worst)
    {
        var store = new ReadingStore();
        var body = JsonNode.Parse(GatewayRequests.ValidBatch("tank-000001", "q"))!.AsObject();
        var template = body["readings"]![0]!;
        body["readings"] = new JsonArray(qualities.Select(quality =>
        {
            var node = template.DeepClone();
            node["quality"] = quality;
            return (JsonNode?)node;
        }).ToArray());
        using (var document = JsonDocument.Parse(body.ToJsonString()))
        {
            store.TryAdd("tank-000001", "q", document.RootElement, caller: null, Now);
        }

        var batch = Assert.Single(DeviceSnapshot.Create(store.Batches, "tank-000001", null, Now)!.NewestBatches);
        Assert.Equal(worst, batch.WorstQuality);
    }

    [Fact]
    public void History_IsNewestFirst_AndCapped()
    {
        var store = new ReadingStore();
        var total = DeviceSnapshot.MaxBatchesShown + 5;
        for (var i = 0; i < total; i++)
        {
            Add(store, "tank-000001", $"m{i:D3}", ("1", "2026-09-14T06:00:00Z"));
        }

        var snapshot = DeviceSnapshot.Create(store.Batches, "tank-000001", null, Now)!;

        Assert.Equal(total, snapshot.Device.MessageCount);
        Assert.Equal(DeviceSnapshot.MaxBatchesShown, snapshot.NewestBatches.Count);
        Assert.Equal($"m{total - 1:D3}", snapshot.NewestBatches[0].MessageId);
        Assert.Equal("m005", snapshot.NewestBatches[^1].MessageId);
    }

    /// <summary>
    /// Every batch the API stores passed the schema, but the store is public. A body put there some
    /// other way is shown with its fields absent rather than failing the page.
    /// </summary>
    [Fact]
    public void BodyWithoutTheExpectedFields_IsShownAsAbsent()
    {
        var store = new ReadingStore();
        using (var empty = JsonDocument.Parse("""{ "readings": [ 1, { "value": "not a number" } ] }"""))
        {
            store.TryAdd("tank-000001", "odd", empty.RootElement, caller: null, Now);
        }

        var snapshot = DeviceSnapshot.Create(store.Batches, "tank-000001", "odd", Now)!;

        var batch = Assert.Single(snapshot.NewestBatches);
        Assert.Null(snapshot.Device.Latest!.Value);
        Assert.Empty(snapshot.Trends);
        Assert.NotEmpty(snapshot.Selected!.Json);
        Assert.Null(batch.ReceivedAt);
        Assert.Null(batch.Firmware);
        var reading = Assert.Single(batch.Readings);
        Assert.Null(reading.Value);
        Assert.Null(reading.MeasuredAt);
    }

    /// <summary>
    /// A device without a value for the column comes last whichever way the table is sorted: an
    /// unknown firmware is not the oldest or the newest. Versions sort as numbers.
    /// </summary>
    [Fact]
    public void Devices_SortedByAColumn_PutThoseWithoutAValueLast_EitherWay()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "a", [("1", "2026-09-14T06:00:00Z")], firmware: "2.10.0");
        Add(store, "tank-000002", "b", [("1", "2026-09-14T06:00:00Z")], firmware: null);
        Add(store, "tank-000003", "c", [("1", "2026-09-14T06:00:00Z")], firmware: "2.9.1");

        Assert.Equal(
            ["tank-000003", "tank-000001", "tank-000002"],
            DashboardSnapshot.Create(store.Batches, Now, new("firmware", false)).Devices.Select(device => device.DeviceId));
        Assert.Equal(
            ["tank-000001", "tank-000003", "tank-000002"],
            DashboardSnapshot.Create(store.Batches, Now, new("firmware", true)).Devices.Select(device => device.DeviceId));
    }

    /// <summary>
    /// Devices the column cannot tell apart keep the default order, A to Z by ID, in either
    /// direction, so the table does not reshuffle on a refresh.
    /// </summary>
    [Fact]
    public void Devices_EqualInTheColumn_KeepTheDefaultOrder()
    {
        var store = new ReadingStore();
        Add(store, "tank-000003", "a", ("1", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000001", "b", ("1", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000002", "c", ("1", "2026-09-14T09:00:00Z"));

        var newestFirst = DashboardSnapshot.Create(store.Batches, Now, new("measured", true));
        var oldestFirst = DashboardSnapshot.Create(store.Batches, Now, new("measured", false));

        Assert.Equal(new TableSort("measured", true), newestFirst.Sort);
        Assert.Equal(["tank-000002", "tank-000001", "tank-000003"], newestFirst.Devices.Select(device => device.DeviceId));
        Assert.Equal(["tank-000001", "tank-000003", "tank-000002"], oldestFirst.Devices.Select(device => device.DeviceId));
        Assert.Equal(DashboardSorts.Devices.Default, DashboardSnapshot.Create(store.Batches, Now).Sort);
    }

    /// <summary>
    /// "Latest" compares devices within the kind of their first series: every level before every
    /// energy reading, and by value within it, since 400 mm and 400 kWh are not comparable.
    /// </summary>
    [Fact]
    public void Devices_ByLatest_CompareWithinAKind()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "a", ("900", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000002", "b", ("400", "2026-09-14T06:00:00Z"));
        using (var charger = JsonDocument.Parse(ContractFixtures.ReadGatewayV2("valid-ev-charger.json")))
        {
            store.TryAdd("charger-000001", "c", charger.RootElement, caller: null, Now);
        }

        Assert.Equal(
            ["tank-000002", "tank-000001", "charger-000001"],
            DashboardSnapshot.Create(store.Batches, Now, new("latest", false)).Devices.Select(device => device.DeviceId));
    }

    /// <summary>
    /// The history is sorted within the newest messages it shows: worst quality first, and messages
    /// of the same quality newest first. Sorting never brings an older message into it.
    /// </summary>
    [Fact]
    public void History_SortedByQuality_IsWorstFirst_WithinTheNewest()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "old-bad", [("1", "2026-09-14T06:00:00Z")], quality: "bad");
        for (var i = 0; i < DeviceSnapshot.MaxBatchesShown - 3; i++)
        {
            Add(store, "tank-000001", $"ok-{i:D3}", ("1", "2026-09-14T06:00:00Z"));
        }

        Add(store, "tank-000001", "uncertain", [("1", "2026-09-14T06:00:00Z")], quality: "uncertain");
        Add(store, "tank-000001", "bad", [("1", "2026-09-14T06:00:00Z")], quality: "bad");
        Add(store, "tank-000001", "newest-ok", ("1", "2026-09-14T06:00:00Z"));

        var history = DeviceSnapshot.Create(store.Batches, "tank-000001", null, Now, new("quality", true))!.NewestBatches;

        Assert.Equal(DeviceSnapshot.MaxBatchesShown, history.Count);
        Assert.Equal(["bad", "uncertain", "newest-ok"], history.Take(3).Select(batch => batch.MessageId));
        Assert.DoesNotContain(history, batch => batch.MessageId == "old-bad");
    }

    /// <summary>
    /// The history's default order is when the platform received each message, newest first: a
    /// message the mock stored late, such as one retried after a failed delivery, sits where the
    /// platform received it.
    /// </summary>
    [Fact]
    public void History_ByDefault_IsNewestReceivedFirst()
    {
        var store = new ReadingStore();
        Add(store, "tank-000001", "received-second", [("1", "2026-09-14T08:00:00Z")], receivedAt: "2026-09-14T08:00:00Z");
        Add(store, "tank-000001", "received-third", [("1", "2026-09-14T09:00:00Z")], receivedAt: "2026-09-14T09:00:00Z");
        Add(store, "tank-000001", "retried", [("1", "2026-09-14T07:00:00Z")], receivedAt: "2026-09-14T07:00:00Z");

        var snapshot = DeviceSnapshot.Create(store.Batches, "tank-000001", null, Now)!;

        Assert.Equal(DashboardSorts.History.Default, snapshot.Sort);
        Assert.Equal(["received-third", "received-second", "retried"], snapshot.NewestBatches.Select(batch => batch.MessageId));
    }

    /// <summary>Stores a valid batch whose readings are the given value and measurement time pairs.</summary>
    private static void Add(ReadingStore store, string deviceId, string messageId, params (string Value, string At)[] readings) =>
        Add(store, deviceId, messageId, readings, firmware: "");

    /// <param name="firmware">The batch's firmware, <c>null</c> to leave it out, or empty for the example's own.</param>
    /// <param name="quality">Every reading's quality, or <c>null</c> for the example's own.</param>
    /// <param name="receivedAt">The batch's <c>receivedAt</c>, or <c>null</c> for the example's own.</param>
    private static void Add(
        ReadingStore store, string deviceId, string messageId, (string Value, string At)[] values,
        string? firmware = "", string? quality = null, string? receivedAt = null)
    {
        var body = JsonNode.Parse(GatewayRequests.ValidBatch(deviceId, messageId, receivedAt))!.AsObject();
        if (firmware is null)
        {
            body.Remove("firmware");
        }
        else if (firmware.Length > 0)
        {
            body["firmware"] = firmware;
        }

        var template = body["readings"]![0]!;
        body["readings"] = new JsonArray(values.Select(reading =>
        {
            var node = template.DeepClone();
            node["value"] = JsonValue.Create(decimal.Parse(reading.Value, System.Globalization.CultureInfo.InvariantCulture));
            node["at"] = reading.At;
            if (quality is not null)
            {
                node["quality"] = quality;
            }

            return (JsonNode?)node;
        }).ToArray());

        using var document = JsonDocument.Parse(body.ToJsonString());
        Assert.True(store.TryAdd(deviceId, messageId, document.RootElement, TestTokenIssuer.Subject, Now));
    }
}
