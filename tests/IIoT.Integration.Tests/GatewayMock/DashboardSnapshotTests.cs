using System.Text.Json;
using System.Text.Json.Nodes;
using IIoT.GatewayMock.Dashboard;
using IIoT.GatewayMock.Readings;
using IIoT.Integration.Tests.Infrastructure;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// What the dashboard reads out of the store: the newest reading per device, and the newest
/// batches. Tested on the snapshot directly, where the ordering rules are visible, rather than by
/// picking values out of the rendered page.
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

        var latest = Assert.Single(DashboardSnapshot.Create(store.Batches, Now).LatestPerDevice);

        Assert.Equal("1500", latest.Reading.Value);
        Assert.Equal("live", latest.Batch.MessageId);
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

        var latest = Assert.Single(DashboardSnapshot.Create(store.Batches, Now).LatestPerDevice);

        Assert.Equal("1", latest.Reading.Value);
    }

    [Fact]
    public void LatestReading_IsPerDevice_OrderedByDeviceId()
    {
        var store = new ReadingStore();
        Add(store, "tank-000002", "b", ("2", "2026-09-14T06:00:00Z"));
        Add(store, "tank-000001", "a", ("1", "2026-09-14T06:00:00Z"));

        var latest = DashboardSnapshot.Create(store.Batches, Now).LatestPerDevice;

        Assert.Equal(["tank-000001", "tank-000002"], latest.Select(entry => entry.DeviceId));
    }

    [Fact]
    public void Batches_AreNewestFirst_AndCapped()
    {
        var store = new ReadingStore();
        var total = DashboardSnapshot.MaxBatchesShown + 5;
        for (var i = 0; i < total; i++)
        {
            Add(store, "tank-000001", $"m{i:D3}", ("1", "2026-09-14T06:00:00Z"));
        }

        var snapshot = DashboardSnapshot.Create(store.Batches, Now);

        Assert.Equal(total, snapshot.BatchCount);
        Assert.Equal(DashboardSnapshot.MaxBatchesShown, snapshot.NewestBatches.Count);
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

        var snapshot = DashboardSnapshot.Create(store.Batches, Now);

        var batch = Assert.Single(snapshot.NewestBatches);
        Assert.Null(batch.ReceivedAt);
        Assert.Null(batch.Firmware);
        var reading = Assert.Single(batch.Readings);
        Assert.Null(reading.Value);
        Assert.Null(reading.MeasuredAt);
    }

    /// <summary>Stores a valid batch whose readings are the given value and measurement time pairs.</summary>
    private static void Add(ReadingStore store, string deviceId, string messageId, params (string Value, string At)[] readings)
    {
        var body = JsonNode.Parse(GatewayRequests.ValidBatch(deviceId, messageId))!.AsObject();
        var template = body["readings"]![0]!;
        body["readings"] = new JsonArray(readings.Select(reading =>
        {
            var node = template.DeepClone();
            node["value"] = JsonValue.Create(decimal.Parse(reading.Value, System.Globalization.CultureInfo.InvariantCulture));
            node["at"] = reading.At;
            return (JsonNode?)node;
        }).ToArray());

        using var document = JsonDocument.Parse(body.ToJsonString());
        Assert.True(store.TryAdd(deviceId, messageId, document.RootElement, TestTokenIssuer.Subject, Now));
    }
}
