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
        var trend = LevelTrend.Create([Reading(1400, "2026-10-30T00:00:00Z"), Reading(1500, "2026-10-30T08:00:00Z")]);

        Assert.Equal(LevelTrend.PlotLeft, trend.Points[0].X);
        Assert.Equal(LevelTrend.PlotRight, trend.Points[1].X);

        // SVG's y grows downwards, so the higher level has the smaller y, and both stay inside.
        Assert.True(trend.Points[1].Y < trend.Points[0].Y);
        Assert.All(trend.Points, point => Assert.InRange(point.Y, LevelTrend.PlotTop, LevelTrend.PlotBottom));

        Assert.Equal(["1400", "1450", "1500"], trend.ValueTicks.Select(tick => tick.Label));
        Assert.Equal("mm", trend.Unit);
        Assert.StartsWith(LevelTrend.Number(LevelTrend.PlotLeft) + "," + LevelTrend.Number(LevelTrend.PlotBottom), trend.Area, StringComparison.Ordinal);
    }

    /// <summary>One reading, or readings that never change, have no range to scale by; they still land inside the plot.</summary>
    [Fact]
    public void Trend_OfOneReading_OrAFlatSeries_StaysInsideThePlot()
    {
        var single = LevelTrend.Create([Reading(1432.5, "2026-10-30T06:00:00Z")]);
        var point = Assert.Single(single.Points);
        Assert.Equal((LevelTrend.PlotLeft + LevelTrend.PlotRight) / 2, point.X);
        Assert.InRange(point.Y, LevelTrend.PlotTop, LevelTrend.PlotBottom);
        Assert.Equal("", single.Area);
        Assert.Equal("1432.5", Assert.Single(single.ValueTicks).Label);

        var flat = LevelTrend.Create([Reading(1000, "2026-10-30T00:00:00Z"), Reading(1000, "2026-10-30T04:00:00Z")]);
        Assert.Equal(flat.Points[0].Y, flat.Points[1].Y);
        Assert.InRange(flat.Points[0].Y, LevelTrend.PlotTop, LevelTrend.PlotBottom);
    }

    [Fact]
    public void Trend_WithoutReadings_IsEmpty()
    {
        var trend = LevelTrend.Create([]);

        Assert.Empty(trend.Points);
        Assert.Null(trend.From);
        Assert.Equal("", trend.Line);
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

    private static ReadingView Reading(double value, string at) =>
        new("level", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "mm", at, "ok",
            DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), value);
}
