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

    /// <summary>
    /// What a series shows, in a sentence or two, for the pages' tooltips and the text under each
    /// chart (#87).
    /// </summary>
    /// <remarks>
    /// From the kind's meaning in the contract (ADR-011), and for a few sensor names whose meaning
    /// is well known, the sensor's: <c>level · oil</c> is the thickness of a separator's oil layer,
    /// not a tank's level. A sensor the dashboard does not know gets its kind's description, so the
    /// text never claims more than the kind says. Like the device type, this is presentation: the
    /// contract gives a sensor no meaning beyond telling a device's series apart.
    /// </remarks>
    public static string Describe(SeriesKey series) => (series.Kind, series.Sensor) switch
    {
        ("level", "oil") =>
            "The thickness of the oil layer separated out on top of the water, in mm. It builds up until the separator is emptied.",
        ("level", "sludge") =>
            "The depth of the sludge settled at the bottom, in mm. It builds up until the separator is emptied.",
        ("level", "shampoo" or "wax") =>
            $"The level in the {series.Sensor} tank, in mm. It falls with every wash, and jumps when the tank is refilled.",
        ("level", "reclaim") =>
            "The level in the reclaim tank, which collects used wash water for reuse, in mm.",
        ("level", _) =>
            "A level: how high the contents stand, in mm.",
        ("energy", _) =>
            "The energy meter's reading: a running total in kWh. The rise between two readings is the energy used in between, and the slope is the power.",
        ("power", _) =>
            "The power at the moment of the reading, in kW.",
        ("volume", "water") =>
            "The water meter's reading: a running total in litres. The rise between two readings is the water used in between.",
        ("volume", _) =>
            "A volume meter's reading: a running total in litres. The rise between two readings is the volume metered in between.",
        ("count", "sessions") =>
            "Charging sessions started, as a running total. The rise between two readings is the sessions started in between.",
        ("count", "cycles") =>
            "Wash cycles run, as a running total. The rise between two readings is the washes in between.",
        ("count", _) =>
            "A counter: a running total of events. The rise between two readings is the events in between.",
        _ =>
            "A kind of reading the dashboard does not know; shown as received."
    };
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
