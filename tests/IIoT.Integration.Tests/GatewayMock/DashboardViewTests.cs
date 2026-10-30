using System.Text.Json;
using IIoT.GatewayMock.Dashboard;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The pieces the dashboard's pages are drawn from: the level chart's geometry, the formatted
/// JSON of a message, how times are written, and the addresses of devices and messages.
/// </summary>
public class DashboardViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trend_SpansThePlot_HigherValuesHigherUp()
    {
        var trend = Trend.Create("level", [Reading(1400, "2026-10-30T00:00:00Z"), Reading(1500, "2026-10-30T08:00:00Z")]);
        var points = Assert.Single(trend.Lines).Points;

        Assert.Equal(Trend.PlotLeft, points[0].X);
        Assert.Equal(Trend.PlotRight, points[1].X);

        // SVG's y grows downwards, so the higher level has the smaller y, and both stay inside.
        Assert.True(points[1].Y < points[0].Y);
        Assert.All(points, point => Assert.InRange(point.Y, Trend.PlotTop, Trend.PlotBottom));

        Assert.Equal(["1400", "1450", "1500"], trend.ValueTicks.Select(tick => tick.Label));
        Assert.Equal("mm", trend.Unit);
        Assert.StartsWith(Trend.Number(Trend.PlotLeft) + "," + Trend.Number(Trend.PlotBottom), trend.Lines[0].Area, StringComparison.Ordinal);
    }

    /// <summary>One reading, or readings that never change, have no range to scale by; they still land inside the plot.</summary>
    [Fact]
    public void Trend_OfOneReading_OrAFlatSeries_StaysInsideThePlot()
    {
        var single = Trend.Create("level", [Reading(1432.5, "2026-10-30T06:00:00Z")]);
        var point = Assert.Single(Assert.Single(single.Lines).Points);
        Assert.Equal((Trend.PlotLeft + Trend.PlotRight) / 2, point.X);
        Assert.InRange(point.Y, Trend.PlotTop, Trend.PlotBottom);
        Assert.Equal("", single.Lines[0].Area);
        Assert.Equal("1432.5", Assert.Single(single.ValueTicks).Label);

        var flat = Trend.Create("level", [Reading(1000, "2026-10-30T00:00:00Z"), Reading(1000, "2026-10-30T04:00:00Z")]);
        Assert.Equal(flat.Lines[0].Points[0].Y, flat.Lines[0].Points[1].Y);
        Assert.InRange(flat.Lines[0].Points[0].Y, Trend.PlotTop, Trend.PlotBottom);
    }

    [Fact]
    public void Trend_WithoutReadings_IsEmpty()
    {
        var trend = Trend.Create("level", []);

        Assert.Empty(trend.Lines);
        Assert.Equal(0, trend.PointCount);
        Assert.Null(trend.From);
    }

    /// <summary>
    /// A kind's sensors share one chart and one scale (ADR-011): the device's only sensor first, then
    /// by name, each in its own colour, and the same value at the same height whichever line it is on.
    /// </summary>
    [Fact]
    public void Trend_OfSeveralSensors_SharesOneScale()
    {
        var trend = Trend.Create("level",
        [
            Reading(212, "2026-10-30T00:00:00Z", sensor: "sludge"),
            Reading(38.5, "2026-10-30T00:00:00Z", sensor: "oil"),
            Reading(212, "2026-10-30T04:00:00Z", sensor: "oil"),
            Reading(40, "2026-10-30T04:00:00Z", sensor: "sludge")
        ]);

        Assert.Equal(["oil", "sludge"], trend.Lines.Select(line => line.Sensor));
        Assert.Equal([0, 1], trend.Lines.Select(line => line.Colour));
        Assert.Equal(4, trend.PointCount);
        Assert.Equal(trend.Lines[1].Points[0].Y, trend.Lines[0].Points[1].Y);
        Assert.Equal(["38.5", "125.3", "212"], trend.ValueTicks.Select(tick => tick.Label));
    }

    /// <summary>
    /// A running total moves little against its size. Where the short labels would repeat, the full
    /// numbers are shown, so the grid lines can still be told apart.
    /// </summary>
    [Fact]
    public void Ticks_ThatWouldRepeat_AreWrittenInFull()
    {
        var trend = Trend.Create("count", [Reading(15872, "2026-10-30T00:00:00Z"), Reading(15937, "2026-10-30T08:00:00Z")]);

        Assert.Equal(["15872", "15904.5", "15937"], trend.ValueTicks.Select(tick => tick.Label));
    }

    [Theory]
    [InlineData(1432.5, "1432.5")]
    [InlineData(9999, "9999")]
    [InlineData(48211.37, "48.2k")]
    [InlineData(1834567, "1.83M")]
    public void Ticks_AreShortEnoughForTheirColumn(double value, string label)
    {
        Assert.Equal(label, Trend.Tick(value));
    }

    [Theory]
    [InlineData("kW.h", "kWh")]
    [InlineData("1", "")]
    [InlineData("mm", "mm")]
    [InlineData("L", "L")]
    public void Units_ReadAsPeopleWriteThem(string ucum, string shown)
    {
        Assert.Equal(shown, DashboardKinds.Unit(ucum));
    }

    [Theory]
    [InlineData("tank", "level")]
    [InlineData("charger", "energy", "power", "count:sessions")]
    [InlineData("wash", "volume:water", "count:cycles", "level:shampoo", "level:wax")]
    [InlineData("separator", "level:oil", "level:sludge")]
    [InlineData("other", "count")]
    public void DeviceType_IsGuessedFromWhatItMeasures(string expected, params string[] series)
    {
        var keys = series.Select(entry => entry.Split(':') is [var kind, var sensor] ? new SeriesKey(kind, sensor) : new SeriesKey(entry, null));

        var type = DeviceType.Of(keys);

        Assert.Equal(expected switch
        {
            "tank" => DeviceType.Tank,
            "charger" => DeviceType.Charger,
            "wash" => DeviceType.CarWash,
            "separator" => DeviceType.OilSeparator,
            _ => DeviceType.Other
        }, type);
    }

    [Fact]
    public void Series_AreOrderedByKindThenSensor_AndLabelled()
    {
        SeriesKey[] keys = [new("count", "sessions"), new("level", "wax"), new("energy", null), new("level", null), new("level", "oil")];

        Assert.Equal(
            ["Level", "Level · oil", "Level · wax", "Energy", "Count · sessions"],
            keys.Order().Select(key => key.Label));
    }

    /// <summary>
    /// The message view shows the body as received: the numbers' own text, keys in their order,
    /// strings as JSON literals with their escapes, and markup as the characters it is, which the
    /// page then HTML-encodes.
    /// </summary>
    [Fact]
    public void Json_KeepsNumbersOrderAndEscapes()
    {
        using var document = JsonDocument.Parse("""{"b":1.50,"a":"<x> \"q\"","list":[true,null],"empty":{}}""");

        var tokens = JsonView.Tokens(document.RootElement);
        var text = string.Concat(tokens.Select(token => token.Text));

        Assert.Equal(
            "{\n  \"b\": 1.50,\n  \"a\": \"<x> \\\"q\\\"\",\n  \"list\": [\n    true,\n    null\n  ],\n  \"empty\": {}\n}",
            text);
        Assert.Contains(new JsonToken("1.50", "number"), tokens);
        Assert.Contains(new JsonToken("\"b\"", "key"), tokens);
        Assert.Contains(new JsonToken("true", "literal"), tokens);
        Assert.Contains(new JsonToken("\"<x> \\\"q\\\"\"", "string"), tokens);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(47 * 3600, "47 h ago")]
    [InlineData(3 * 86400, "3 d ago")]
    [InlineData(-10 * 60, "ahead of the mock's clock")]
    public void Ago_IsRough_AndHonestAboutAClockAhead(int secondsAgo, string expected)
    {
        Assert.Equal(expected, DashboardFormat.Ago(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Times_FromABody_AreFormattedWhenReadable_OtherwiseShownAsSent()
    {
        Assert.Equal("30 Oct 2026, 10:30:00 UTC", DashboardFormat.Instant("2026-10-30T12:30:00+02:00"));
        Assert.Equal("yesterday-ish", DashboardFormat.Instant("yesterday-ish"));
        Assert.Equal(DashboardFormat.Absent, DashboardFormat.Instant(null));
        Assert.Null(DashboardFormat.Ago("yesterday-ish", Now));
    }

    /// <summary>
    /// IDs travel escaped in the query string, so one with <c>/</c>, <c>%</c>, <c>&amp;</c> or
    /// markup in it addresses exactly itself.
    /// </summary>
    [Fact]
    public void Links_EscapeTheIds()
    {
        Assert.Equal("/dashboard/device?id=tank%2F1%25%26%3Cb%3E", DashboardLinks.Device("tank/1%&<b>"));
        Assert.Equal("/dashboard/device?id=tank-1&message=a%26b%3D1", DashboardLinks.Message("tank-1", "a&b=1"));
    }

    private static ReadingView Reading(double value, string at, string? sensor = null) =>
        new("level", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "mm", at, "ok",
            DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), value, sensor);
}
