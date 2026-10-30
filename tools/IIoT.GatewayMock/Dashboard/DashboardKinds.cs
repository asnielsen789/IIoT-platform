namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// One series of a device's readings: a kind and, where the device has several sensors of that
/// kind, the sensor (ADR-011). An oil separator has two level series, <c>oil</c> and <c>sludge</c>.
/// </summary>
/// <remarks>Ordered by kind (<see cref="DashboardKinds.Order"/>), then by sensor, a device's only sensor first.</remarks>
public readonly record struct SeriesKey(string? Kind, string? Sensor) : IComparable<SeriesKey>
{
    /// <summary>How the pages name the series: <c>Level</c>, or <c>Level · oil</c>.</summary>
    public string Label => Sensor is null ? DashboardKinds.Label(Kind) : $"{DashboardKinds.Label(Kind)} · {Sensor}";

    public int CompareTo(SeriesKey other)
    {
        var byKind = DashboardKinds.Order(Kind).CompareTo(DashboardKinds.Order(other.Kind));
        if (byKind != 0)
        {
            return byKind;
        }

        var byName = string.CompareOrdinal(Kind, other.Kind);
        return byName != 0 ? byName : string.CompareOrdinal(Sensor, other.Sensor);
    }
}

/// <summary>How the dashboard names, orders and labels the contract's reading kinds (ADR-011).</summary>
internal static class DashboardKinds
{
    private static readonly string[] Known = ["level", "energy", "power", "volume", "count"];

    /// <summary>The contract's order; a kind the dashboard does not know comes last.</summary>
    public static int Order(string? kind) => Array.IndexOf(Known, kind) is var index and >= 0 ? index : Known.Length;

    public static string Label(string? kind) => kind switch
    {
        "level" => "Level",
        "energy" => "Energy",
        "power" => "Power",
        "volume" => "Volume",
        "count" => "Count",
        null or "" => DashboardFormat.Absent,
        _ => kind
    };

    /// <summary>
    /// A UCUM code as people read it: <c>kW.h</c> is kWh, and <c>1</c>, a pure count, has no unit
    /// to show. Every other code reads as itself.
    /// </summary>
    public static string Unit(string? unit) => unit switch
    {
        "kW.h" => "kWh",
        "1" or null => "",
        _ => unit
    };

    /// <summary>Whether the kind is a meter's running total rather than a reading of the moment.</summary>
    public static bool IsCumulative(string? kind) => kind is "energy" or "volume" or "count";
}

/// <summary>
/// What kind of device the pages present, guessed from the series it sends. Presentation only:
/// the contract has no device type, and a receiver judges readings by kind, not by device.
/// </summary>
/// <param name="Label">How the pages name the type.</param>
/// <param name="Icon">The <see cref="Icon"/> it is drawn with.</param>
public sealed record DeviceType(string Label, string Icon)
{
    public static readonly DeviceType Tank = new("Tank", "tank");
    public static readonly DeviceType Charger = new("EV charger", "bolt");
    public static readonly DeviceType CarWash = new("Car wash", "car");
    public static readonly DeviceType OilSeparator = new("Oil separator", "layers");
    public static readonly DeviceType Other = new("Device", "chip");

    public static DeviceType Of(IEnumerable<SeriesKey> series)
    {
        var keys = series.ToList();

        if (keys.Any(key => key.Kind is "energy" or "power"))
        {
            return Charger;
        }

        if (keys.Any(key => key.Kind == "volume"))
        {
            return CarWash;
        }

        if (keys.Count > 0 && keys.All(key => key.Kind == "level"))
        {
            return keys.Any(key => key.Sensor is "oil" or "sludge") ? OilSeparator : Tank;
        }

        return Other;
    }
}
