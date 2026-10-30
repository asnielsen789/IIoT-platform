using System.Globalization;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// A device's readings laid out for the level chart, in the SVG's own coordinates.
/// </summary>
/// <remarks>
/// <para>
/// The geometry is computed here, on the server, and the page draws it as inline SVG: a chart
/// without a script, which the dashboard's Content-Security-Policy would refuse anyway. Time runs
/// along the x axis by each reading's own <c>at</c>, so backfill falls into place among the
/// readings around it rather than at the end.
/// </para>
/// <para>
/// The SVG holds the plot only, in abstract units, and is stretched to the width of the page and
/// a fixed height. Its labels are HTML placed beside it by percentage, so they keep their size on a
/// phone and on a wide screen alike, where text inside a scaled SVG would shrink or grow with it.
/// </para>
/// </remarks>
/// <param name="Points">The readings, oldest first, each with its position.</param>
/// <param name="ValueTicks">Labelled horizontal grid lines: the lowest, middle and highest value, or the one value of a flat series.</param>
/// <param name="Unit">The readings' unit, from the first that has one.</param>
/// <param name="From">The oldest reading's measurement time, or <c>null</c> without readings.</param>
/// <param name="To">The newest reading's measurement time.</param>
public sealed record LevelTrend(
    IReadOnlyList<TrendPoint> Points,
    IReadOnlyList<TrendTick> ValueTicks,
    string? Unit,
    DateTimeOffset? From,
    DateTimeOffset? To)
{
    public const double Width = 1000;
    public const double Height = 100;

    // A margin inside the plot, so the dots at either end are not cut in half by its edge.
    public const double PlotLeft = 12;
    public const double PlotRight = Width - 12;
    public const double PlotTop = 0;
    public const double PlotBottom = Height;

    /// <summary>The <c>points</c> attribute of the line through the readings.</summary>
    public string Line => string.Join(' ', Points.Select(point => $"{Number(point.X)},{Number(point.Y)}"));

    /// <summary>The <c>points</c> attribute of the shaded area under the line; empty for fewer than two readings.</summary>
    public string Area => Points.Count < 2
        ? ""
        : $"{Number(Points[0].X)},{Number(PlotBottom)} {Line} {Number(Points[^1].X)},{Number(PlotBottom)}";

    /// <summary>A coordinate or a value as SVG and the page write it: invariant, at most one decimal.</summary>
    public static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A height in the plot as a percentage from its top, for placing a label beside it.</summary>
    public static string Percent(double y) => (y / Height * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <param name="readings">Oldest first, each with a <see cref="ReadingView.MeasuredAt"/> and a <see cref="ReadingView.Number"/>.</param>
    internal static LevelTrend Create(IReadOnlyList<ReadingView> readings)
    {
        if (readings.Count == 0)
        {
            return new LevelTrend([], [], null, null, null);
        }

        var min = readings.Min(reading => reading.Number!.Value);
        var max = readings.Max(reading => reading.Number!.Value);

        // Room above and below, so the line never runs along the frame. A flat series has no range
        // to take a share of, so it gets a band of its own around its one value.
        var padding = max > min ? (max - min) * 0.15 : Math.Max(Math.Abs(max) * 0.05, 1);
        var low = min - padding;
        var high = max + padding;

        var from = readings[0].MeasuredAt!.Value;
        var to = readings[^1].MeasuredAt!.Value;
        var span = (to - from).TotalSeconds;

        double X(DateTimeOffset at) => span > 0
            ? PlotLeft + (at - from).TotalSeconds / span * (PlotRight - PlotLeft)
            : (PlotLeft + PlotRight) / 2;

        double Y(double value) => PlotBottom - (value - low) / (high - low) * (PlotBottom - PlotTop);

        var points = readings
            .Select(reading => new TrendPoint(X(reading.MeasuredAt!.Value), Y(reading.Number!.Value), reading))
            .ToList();

        double[] tickValues = max > min ? [min, (min + max) / 2, max] : [min];
        var ticks = tickValues.Select(value => new TrendTick(Y(value), Number(value))).ToList();

        return new LevelTrend(points, ticks, readings.Select(reading => reading.Unit).FirstOrDefault(unit => unit is not null), from, to);
    }
}

/// <summary>One reading on the chart.</summary>
public sealed record TrendPoint(double X, double Y, ReadingView Reading);

/// <summary>A labelled grid line at height <paramref name="Y"/>.</summary>
public sealed record TrendTick(double Y, string Label);
