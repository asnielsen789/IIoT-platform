namespace IIoT.TestClient.Fleet;

/// <summary>
/// A way a simulated sensor misbehaves (#88), so the platform, the dashboard and the test suite
/// can be seen coping with a real fleet's bad sensors, not only its healthy ones.
/// </summary>
/// <remarks>
/// Every fault but <see cref="OutOfRange"/> keeps the readings within the contract: an implausible
/// value is still a valid one (ADR-004), and telling a faulty sensor from a real change is analysis,
/// which belongs downstream or in alerting, not in validation.
/// </remarks>
public enum SensorFault
{
    /// <summary><c>stuck</c>: the same value for eight hours a day, whatever the contents do.</summary>
    Stuck,

    /// <summary><c>spikes</c>: three times a day a single reading far above the trend, and back at the next.</summary>
    Spikes,

    /// <summary><c>noisy</c>: jitter well above the sensor's normal, on every reading.</summary>
    Noisy,

    /// <summary><c>drift</c>: an offset that grows by the hour, until the weekly service resets it on Monday at midnight UTC.</summary>
    Drift,

    /// <summary><c>dropouts</c>: no reading for five hours a day, and one in ten missing at other times.</summary>
    Dropouts,

    /// <summary><c>quality</c>: ten hours a day in which the device flags its readings, five <c>uncertain</c>, then five <c>bad</c>.</summary>
    Quality,

    /// <summary><c>out-of-range</c>: once a day a value below zero, which the contract rejects, and with it the whole message.</summary>
    OutOfRange
}

/// <summary>The faults' names in a fleet file.</summary>
public static class SensorFaultNames
{
    private static readonly (SensorFault Fault, string Name)[] Names =
    [
        (SensorFault.Stuck, "stuck"),
        (SensorFault.Spikes, "spikes"),
        (SensorFault.Noisy, "noisy"),
        (SensorFault.Drift, "drift"),
        (SensorFault.Dropouts, "dropouts"),
        (SensorFault.Quality, "quality"),
        (SensorFault.OutOfRange, "out-of-range")
    ];

    /// <summary>Every name, as an error message lists them.</summary>
    public static string All => string.Join(", ", Names.Select(entry => entry.Name));

    public static string Name(SensorFault fault) => Names.Single(entry => entry.Fault == fault).Name;

    /// <summary>The fault <paramref name="name"/> names, or <c>null</c> for none.</summary>
    public static SensorFault? Parse(string name)
    {
        foreach (var (fault, faultName) in Names)
        {
            if (string.Equals(faultName, name, StringComparison.Ordinal))
            {
                return fault;
            }
        }

        return null;
    }
}

/// <summary>
/// A device's faults, applied to what its healthy behaviour reads. They affect one sensor: the
/// device's first that measures a quantity of the moment, a level or a power, never a meter, whose
/// running total cannot go down (ADR-011). A tank's level, a charger's power, a car wash's shampoo
/// tank and a separator's oil layer.
/// </summary>
/// <remarks>
/// <para>
/// The faults draw from a generator of their own, seeded from the device, so the faulty device sends
/// exactly what its healthy self would, except for that one sensor. When a fault strikes is tied to
/// the hour of the day, counted from an hour the device's seed picks, so any day of its history
/// holds every fault it has, wherever the day's window falls.
/// </para>
/// <para>
/// The values are clamped to the contract's range for the kind, except by
/// <see cref="SensorFault.OutOfRange"/>, and rounded to a tenth, as the healthy readings are.
/// </para>
/// </remarks>
internal sealed class FaultySensor
{
    /// <summary>The Monday the weekly service resets the drift on, and every Monday after.</summary>
    private static readonly DateTimeOffset ServiceEpoch = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly IReadOnlySet<SensorFault> _faults;
    private readonly Random _random;
    private readonly int _firstHour;
    private double? _stuckAt;

    public FaultySensor(FleetDevice device)
    {
        _faults = device.Faults.ToHashSet();
        _random = new Random(device.Seed ^ 0x0FA017);
        _firstHour = device.Seed % 24;
    }

    /// <summary>
    /// The readings of the sample taken at <paramref name="at"/>, with the faults applied: the
    /// faulty sensor's reading changed, or left out by a dropout.
    /// </summary>
    public IReadOnlyList<Sample> Apply(DateTimeOffset at, IReadOnlyList<Sample> readings)
    {
        var index = readings.ToList().FindIndex(reading => reading.Type is "level" or "power");
        if (_faults.Count == 0 || index < 0)
        {
            return readings;
        }

        var faulty = Apply(at, readings[index]);
        var result = readings.ToList();
        if (faulty is null)
        {
            result.RemoveAt(index);
        }
        else
        {
            result[index] = faulty;
        }

        return result;
    }

    /// <returns>The reading as the faulty sensor reports it, or <c>null</c> for none.</returns>
    private Sample? Apply(DateTimeOffset at, Sample reading)
    {
        var hour = (at.UtcDateTime.Hour - _firstHour + 24) % 24;
        var max = reading.Type == "level" ? 20_000 : 1_000;
        var value = reading.Value;

        if (_faults.Contains(SensorFault.Drift))
        {
            var sinceService = (at - ServiceEpoch).TotalHours % (7 * 24);
            value += sinceService * (reading.Type == "level" ? 1.5 : 0.05);
        }

        if (_faults.Contains(SensorFault.Noisy))
        {
            value += Gaussian() * (reading.Type == "level" ? 40 : 4);
        }

        if (_faults.Contains(SensorFault.Stuck))
        {
            _stuckAt = hour < 8 ? _stuckAt ?? value : null;
            value = _stuckAt ?? value;
        }

        if (_faults.Contains(SensorFault.Spikes) && hour is 3 or 10 or 17)
        {
            value += reading.Type == "level" ? Between(4_000, 9_000) : Between(300, 700);
        }

        value = Math.Round(Math.Clamp(value, 0, max), 1, MidpointRounding.AwayFromZero);

        var quality = reading.Quality;
        if (_faults.Contains(SensorFault.Quality) && hour is >= 6 and < 16)
        {
            quality = hour < 11 ? "uncertain" : "bad";
        }

        if (_faults.Contains(SensorFault.OutOfRange) && hour == 20)
        {
            // A probe whose zero point has failed reads below the bottom of the tank.
            value = -Math.Round(Between(5, 50), 1, MidpointRounding.AwayFromZero);
        }

        // Drawn on every reading, so a dropout does not change what the generator gives the next one.
        var droppedAtRandom = _random.NextDouble() < 0.1;
        if (_faults.Contains(SensorFault.Dropouts) && (hour is >= 12 and < 17 || droppedAtRandom))
        {
            return null;
        }

        return reading with { Value = value, Quality = quality };
    }

    private double Between(double low, double high) => low + _random.NextDouble() * (high - low);

    /// <summary>A standard normal draw (Box–Muller).</summary>
    private double Gaussian() =>
        Math.Sqrt(-2 * Math.Log(1 - _random.NextDouble())) * Math.Cos(2 * Math.PI * _random.NextDouble());
}
