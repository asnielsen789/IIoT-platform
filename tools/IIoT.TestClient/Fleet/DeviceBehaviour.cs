namespace IIoT.TestClient.Fleet;

/// <summary>One measurement a simulated device takes, before it is written into a message.</summary>
/// <param name="Type">The device contract's measurement type.</param>
/// <param name="Value">The value, already rounded as the device would send it.</param>
/// <param name="Unit">The type's UCUM unit.</param>
/// <param name="Sensor">Which sensor, or <c>null</c> for a device's only one of the type.</param>
/// <param name="Quality">The device's own assessment.</param>
public sealed record Sample(string Type, double Value, string Unit, string? Sensor = null, string Quality = "ok");

/// <summary>
/// How one simulated device's readings move over time. Seeded, so a device behaves the same on
/// every run; stateful, so its counters only ever grow and its tanks drain and are refilled.
/// </summary>
/// <remarks>
/// The numbers are plausible rather than measured: they exist to make a dashboard and a test
/// suite meaningful, not to model any real site. Counters are cumulative totals (ADR-011).
/// </remarks>
public abstract class DeviceBehaviour
{
    protected DeviceBehaviour(int seed)
    {
        Random = new Random(seed);
    }

    /// <summary>The device's own generator; everything it does comes from it.</summary>
    protected Random Random { get; }

    /// <summary>The behaviour for <paramref name="device"/>'s profile.</summary>
    public static DeviceBehaviour For(FleetDevice device) => device.Profile switch
    {
        DeviceProfile.Tank => new TankBehaviour(device.Seed),
        DeviceProfile.Charger => new ChargerBehaviour(device.Seed),
        DeviceProfile.CarWash => new CarWashBehaviour(device.Seed),
        _ => new OilSeparatorBehaviour(device.Seed)
    };

    /// <summary>Advances the device to <paramref name="at"/>, <paramref name="elapsed"/> after the previous sample, and reads its sensors.</summary>
    public abstract IReadOnlyList<Sample> Read(DateTimeOffset at, TimeSpan elapsed);

    /// <summary>A number between <paramref name="low"/> and <paramref name="high"/>.</summary>
    protected double Between(double low, double high) => low + Random.NextDouble() * (high - low);

    /// <summary>Busier by day, quieter at night: 1.5 from 07:00 to 19:00 UTC, otherwise 0.4.</summary>
    protected static double Daytime(DateTimeOffset at) => at.UtcDateTime.Hour is >= 7 and < 19 ? 1.5 : 0.4;

    protected static double Round(double value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}

/// <summary>A storage tank: drained by use, refilled by a delivery when it runs low.</summary>
internal sealed class TankBehaviour : DeviceBehaviour
{
    private readonly double _usePerHour;
    private double _level;

    public TankBehaviour(int seed) : base(seed)
    {
        _level = Between(900, 2200);
        _usePerHour = Between(6, 16);
    }

    public override IReadOnlyList<Sample> Read(DateTimeOffset at, TimeSpan elapsed)
    {
        _level -= _usePerHour * Daytime(at) * elapsed.TotalHours * Between(0.7, 1.3);
        if (_level < 350)
        {
            _level = Between(2150, 2350);
        }

        return [new Sample("level", Round(_level, 1), "mm", Quality: Random.NextDouble() < 0.02 ? "uncertain" : "ok")];
    }
}

/// <summary>
/// An EV charging station: sessions start more often by day, draw near the station's rated power
/// while they last, and add to its energy meter and its session counter.
/// </summary>
internal sealed class ChargerBehaviour : DeviceBehaviour
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);
    private static readonly double[] Ratings = [11, 22, 22, 50, 150];

    private readonly double _rated;
    private double _energy;
    private long _sessions;
    private TimeSpan _sessionLeft;
    private double _sessionPower;

    public ChargerBehaviour(int seed) : base(seed)
    {
        _rated = Ratings[Random.Next(Ratings.Length)];
        _energy = Round(Between(15_000, 75_000), 2);
        _sessions = Random.Next(500, 3500);
    }

    public override IReadOnlyList<Sample> Read(DateTimeOffset at, TimeSpan elapsed)
    {
        // Simulated in quarter hours, so a session can start and end between two samples.
        for (var time = at - elapsed + Step; time <= at; time += Step)
        {
            if (_sessionLeft <= TimeSpan.Zero && Random.NextDouble() < 0.12 * Daytime(time))
            {
                _sessions++;
                _sessionPower = _rated * Between(0.6, 0.95);
                _sessionLeft = TimeSpan.FromMinutes(_rated >= 50 ? Between(20, 60) : Between(45, 240));
            }

            if (_sessionLeft > TimeSpan.Zero)
            {
                _energy += _sessionPower * Step.TotalHours;
                _sessionLeft -= Step;
            }
        }

        var power = _sessionLeft > TimeSpan.Zero ? Round(_sessionPower * Between(0.95, 1.0), 1) : 0;

        return
        [
            new Sample("energy", Round(_energy, 2), "kW.h"),
            new Sample("power", power, "kW"),
            new Sample("count", _sessions, "1", "sessions")
        ];
    }
}

/// <summary>
/// A car wash: each wash uses fresh water and some shampoo and wax, the detergent tanks are refilled
/// when they run low, and the water-reclaim tank rises and falls.
/// </summary>
internal sealed class CarWashBehaviour : DeviceBehaviour
{
    private double _water;
    private long _cycles;
    private double _shampoo;
    private double _wax;
    private double _reclaim;

    public CarWashBehaviour(int seed) : base(seed)
    {
        _water = Math.Round(Between(500_000, 2_500_000));
        _cycles = Random.Next(5_000, 25_000);
        _shampoo = Between(500, 750);
        _wax = Between(500, 750);
        _reclaim = Between(1200, 1700);
    }

    public override IReadOnlyList<Sample> Read(DateTimeOffset at, TimeSpan elapsed)
    {
        var washes = (int)Math.Round(Between(0, 6) * Daytime(at) * elapsed.TotalHours);

        _cycles += washes;
        _water += Math.Round(washes * Between(110, 170));
        _shampoo -= washes * Between(1.6, 2.4);
        _wax -= washes * Between(1.0, 1.6);
        _reclaim = Math.Clamp(_reclaim + Between(-60, 80), 900, 1900);

        if (_shampoo < 120)
        {
            _shampoo = Between(700, 740);
        }

        if (_wax < 120)
        {
            _wax = Between(700, 740);
        }

        return
        [
            new Sample("volume", _water, "L", "water"),
            new Sample("count", _cycles, "1", "cycles"),
            new Sample("level", Round(_shampoo, 1), "mm", "shampoo"),
            new Sample("level", Round(_wax, 1), "mm", "wax"),
            new Sample("level", Round(_reclaim, 1), "mm", "reclaim", Random.NextDouble() < 0.03 ? "uncertain" : "ok")
        ];
    }
}

/// <summary>
/// An oil separator: the oil layer and the sludge build up slowly, faster while the site is busy,
/// until a service empties it. The oil probe fouls now and then, and says so.
/// </summary>
internal sealed class OilSeparatorBehaviour : DeviceBehaviour
{
    private double _oil;
    private double _sludge;

    public OilSeparatorBehaviour(int seed) : base(seed)
    {
        _oil = Between(5, 60);
        _sludge = Between(50, 220);
    }

    public override IReadOnlyList<Sample> Read(DateTimeOffset at, TimeSpan elapsed)
    {
        _oil += Between(0.05, 0.25) * Daytime(at) * elapsed.TotalHours;
        _sludge += Between(0.03, 0.12) * elapsed.TotalHours;

        if (_oil > 180)
        {
            _oil = Between(1, 4);
            _sludge = Between(15, 30);
        }

        return
        [
            new Sample("level", Round(_oil, 1), "mm", "oil", Random.NextDouble() < 0.01 ? "bad" : "ok"),
            new Sample("level", Round(_sludge, 1), "mm", "sludge")
        ];
    }
}
