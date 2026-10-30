using System.Globalization;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// One kind of a device's readings laid out for a chart, in the SVG's own coordinates: a line per
/// sensor, on one shared scale (ADR-011).
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
/// <para>
/// A kind's sensors share the chart and its scale because they share the unit: an oil separator's
/// oil and sludge, a car wash's three tanks. For a cumulative kind the line is the meter's total,
/// so its slope is the rate of use.
/// </para>
/// </remarks>
/// <param name="Kind">The kind charted.</param>
/// <param name="Unit">The readings' unit, from the first that has one.</param>
/// <param name="Lines">One line per sensor, the device's only sensor first, then by name.</param>
/// <param name="ValueTicks">Labelled horizontal grid lines: the lowest, middle and highest value, or the one value of a flat series.</param>
/// <param name="From">The oldest reading's measurement time, or <c>null</c> without readings.</param>
/// <param name="To">The newest reading's measurement time.</param>
public sealed record Trend(
    string Kind,
    string? Unit,
    IReadOnlyList<TrendLine> Lines,
    IReadOnlyList<TrendTick> ValueTicks,
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

    /// <summary>How many line colours the stylesheet defines; further lines reuse them.</summary>
    public const int Colours = 5;

    /// <summary>Every reading on the chart.</summary>
    public int PointCount => Lines.Sum(line => line.Points.Count);

    /// <summary>A coordinate as SVG writes it: invariant, at most one decimal.</summary>
    public static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A height in the plot as a percentage from its top, for placing a label beside it.</summary>
    public static string Percent(double y) => (y / Height * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A value for an axis label, short enough for its column: 1834567 is 1.83M, 48211.37 is
    /// 48.2k. The tooltips keep the value as it was sent.
    /// </summary>
    public static string Tick(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0.#", CultureInfo.InvariantCulture)
    };

    /// <param name="kind">The kind the readings share.</param>
    /// <param name="readings">Any order, each with a <see cref="ReadingView.MeasuredAt"/> and a <see cref="ReadingView.Number"/>.</param>
    internal static Trend Create(string kind, IReadOnlyList<ReadingView> readings)
    {
        if (readings.Count == 0)
        {
            return new Trend(kind, null, [], [], null, null);
        }

        var min = readings.Min(reading => reading.Number!.Value);
        var max = readings.Max(reading => reading.Number!.Value);

        // Room above and below, so a line never runs along the frame. A flat series has no range
        // to take a share of, so it gets a band of its own around its one value.
        var padding = max > min ? (max - min) * 0.15 : Math.Max(Math.Abs(max) * 0.05, 1);
        var low = min - padding;
        var high = max + padding;

        var from = readings.Min(reading => reading.MeasuredAt!.Value);
        var to = readings.Max(reading => reading.MeasuredAt!.Value);
        var span = (to - from).TotalSeconds;

        double X(DateTimeOffset at) => span > 0
            ? PlotLeft + (at - from).TotalSeconds / span * (PlotRight - PlotLeft)
            : (PlotLeft + PlotRight) / 2;

        double Y(double value) => PlotBottom - (value - low) / (high - low) * (PlotBottom - PlotTop);

        var lines = readings
            .GroupBy(reading => reading.Sensor)
            .OrderBy(sensor => sensor.Key, StringComparer.Ordinal)
            .Select((sensor, index) => new TrendLine(
                sensor.Key,
                index % Colours,
                sensor.OrderBy(reading => reading.MeasuredAt)
                    .Select(reading => new TrendPoint(X(reading.MeasuredAt!.Value), Y(reading.Number!.Value), reading))
                    .ToList()))
            .ToList();

        // Short labels, unless they would repeat: a running total of 15872 to 15937 is 15.9k three
        // times over, and then only the full numbers tell the grid lines apart.
        double[] tickValues = max > min ? [min, (min + max) / 2, max] : [min];
        var labels = tickValues.Select(Tick).ToList();
        if (labels.Distinct(StringComparer.Ordinal).Count() < labels.Count)
        {
            labels = tickValues.Select(value => value.ToString("0.#", CultureInfo.InvariantCulture)).ToList();
        }

        var ticks = tickValues.Select((value, index) => new TrendTick(Y(value), labels[index])).ToList();

        return new Trend(kind, readings.Select(reading => reading.Unit).FirstOrDefault(unit => unit is not null), lines, ticks, from, to);
    }
}

/// <summary>One sensor's readings on a chart, oldest first.</summary>
/// <param name="Sensor">The sensor, or <c>null</c> for the device's only one of the kind.</param>
/// <param name="Colour">Which of the stylesheet's line colours it is drawn in, 0 to <see cref="Trend.Colours"/> − 1.</param>
/// <param name="Points">Its readings, each with its position.</param>
public sealed record TrendLine(string? Sensor, int Colour, IReadOnlyList<TrendPoint> Points)
{
    /// <summary>The <c>points</c> attribute of the line through the readings.</summary>
    public string Line => string.Join(' ', Points.Select(point => $"{Trend.Number(point.X)},{Trend.Number(point.Y)}"));

    /// <summary>The <c>points</c> attribute of the shaded area under the line; empty for fewer than two readings.</summary>
    public string Area => Points.Count < 2
        ? ""
        : $"{Trend.Number(Points[0].X)},{Trend.Number(Trend.PlotBottom)} {Line} {Trend.Number(Points[^1].X)},{Trend.Number(Trend.PlotBottom)}";
}

/// <summary>One reading on a chart.</summary>
public sealed record TrendPoint(double X, double Y, ReadingView Reading);

/// <summary>A labelled grid line at height <paramref name="Y"/>.</summary>
public sealed record TrendTick(double Y, string Label);
