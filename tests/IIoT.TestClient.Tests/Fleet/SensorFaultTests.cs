using System.Globalization;
using System.Text;
using System.Text.Json;
using IIoT.Core.Validation;
using IIoT.TestClient.Fleet;

namespace IIoT.TestClient.Tests.Fleet;

/// <summary>
/// The fleet's faulty sensors (#88). Each faulty device of the reference fleet is compared with its
/// healthy self, the same device without faults: the fault shows on its one faulty sensor, and
/// nothing else the device sends changes. Messages are checked with the platform's own validator,
/// which must accept every one of them except the ones an out-of-range reading puts outside the
/// contract, and must name that reading when it rejects them.
/// </summary>
public class SensorFaultTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 18, 23, 0, TimeSpan.Zero);

    public static TheoryData<string> FaultyReferenceDevices()
    {
        var data = new TheoryData<string>();
        foreach (var device in FleetFile.Load(FleetFile.ReferencePath).Where(device => device.Faults.Count > 0))
        {
            data.Add(device.Id);
        }

        return data;
    }

    [Fact]
    public void ReferenceFleet_HasEachFaultOnce_OnDevicesTheOtherTestsDoNotUse()
    {
        var fleet = FleetFile.Load(FleetFile.ReferencePath);
        var faulty = fleet.Where(device => device.Faults.Count > 0).ToList();

        Assert.Equal(Enum.GetValues<SensorFault>().Order(), faulty.SelectMany(device => device.Faults).Order());
        Assert.All(faulty, device => Assert.Single(device.Faults));

        // The devices with their own identities on staging, and those the profile tests read, stay healthy.
        string[] healthy = ["tank-000001", "tank-000002", "tank-000003", "charger-000001", "wash-000001", "wash-000002", "separator-000001"];
        Assert.All(healthy, id => Assert.Empty(fleet.Single(device => device.Id == id).Faults));
    }

    /// <summary>
    /// A fault changes the faulty sensor only: every other series of the device is reading for
    /// reading what its healthy self sends, and every message is valid but those carrying an
    /// out-of-range reading.
    /// </summary>
    [Theory]
    [MemberData(nameof(FaultyReferenceDevices))]
    public void FaultyDevice_DiffersFromItsHealthySelf_OnlyOnItsFaultySensor(string deviceId)
    {
        var device = Device(deviceId);
        var faulty = FleetHistory.Create(device, FleetSchedule.Default, Now);
        var healthy = FleetHistory.Create(device with { Faults = [] }, FleetSchedule.Default, Now);

        var target = Target(Readings(healthy));
        Assert.Equal(
            Readings(healthy).Where(reading => reading.Series != target),
            Readings(faulty).Where(reading => reading.Series != target));

        var rejected = faulty.Select(message => TelemetryValidator.Validate(Encoding.UTF8.GetString(message.Payload))).Count(result => !result.IsValid);
        Assert.Equal(device.Faults.Contains(SensorFault.OutOfRange) ? 1 : 0, rejected);
    }

    [Fact]
    public void Stuck_HoldsOneValueForEightHours_WhileTheContentsMove()
    {
        var (faulty, healthy) = TargetSeries("tank-000005");

        Assert.Equal(8, LongestRun(faulty.Select(reading => reading.Value)));
        Assert.True(LongestRun(healthy.Select(reading => reading.Value)) < 3, "the healthy tank's level moves every hour");
    }

    [Fact]
    public void Spikes_AreThreeReadingsFarAboveTheTrend_AndTheRestUntouched()
    {
        var (faulty, healthy) = TargetSeries("separator-000002");

        var differences = faulty.Zip(healthy, (bad, good) => bad.Value - good.Value).ToList();
        Assert.Equal(3, differences.Count(difference => difference > 1_000));
        Assert.Equal(faulty.Count - 3, differences.Count(difference => difference == 0));
    }

    [Fact]
    public void Noisy_JittersMostReadings_WellAboveTheSensorsOwn()
    {
        var (faulty, healthy) = TargetSeries("charger-000004");

        var differences = faulty.Zip(healthy, (bad, good) => Math.Abs(bad.Value - good.Value)).ToList();
        Assert.True(differences.Count(difference => difference > 0) >= faulty.Count / 2, "most readings should be off");
        Assert.True(differences.Average() > 1, $"the noise should average more than 1 kW, not {differences.Average():0.00}");
    }

    /// <summary>The offset grows with every hour since the last weekly service, which is not in this window.</summary>
    [Fact]
    public void Drift_IsAnOffsetThatGrowsByTheHour()
    {
        var (faulty, healthy) = TargetSeries("tank-000007");

        var offsets = faulty.Zip(healthy, (bad, good) => bad.Value - good.Value).ToList();
        Assert.All(offsets, offset => Assert.True(offset > 0, $"the offset should be positive, not {offset}"));
        Assert.Equal(offsets.Order(), offsets);
        Assert.True(offsets[^1] - offsets[0] > 30, "a day should add more than 30 mm");
    }

    /// <summary>
    /// Five hours without a reading, and a few more missing. A tank has no other sensor, so a message
    /// that would have carried nothing is not sent, and the sequence numbers still run without a gap.
    /// </summary>
    [Fact]
    public void Dropouts_LeaveGapsInTheSeries_AndNoEmptyMessage()
    {
        var history = FleetHistory.Create(Device("tank-000006"), FleetSchedule.Default, Now);
        var times = Readings(history).Select(reading => DateTimeOffset.Parse(reading.MeasuredAt, CultureInfo.InvariantCulture)).ToList();

        Assert.True(times.Count <= 24 - 5, $"at least five of the day's 24 readings should be missing, not {24 - times.Count}");
        Assert.Contains(times.Zip(times.Skip(1), (earlier, later) => later - earlier), gap => gap >= TimeSpan.FromHours(5));

        var sequences = new List<long>();
        foreach (var message in history)
        {
            var result = TelemetryValidator.Validate(Encoding.UTF8.GetString(message.Payload));
            Assert.True(result.IsValid, result.Error);
            sequences.Add(result.Message!.Sequence!.Value);
        }

        Assert.Equal(Enumerable.Range(0, sequences.Count).Select(index => sequences[0] + index), sequences);
    }

    [Fact]
    public void Quality_FlagsFiveHoursUncertain_ThenFiveBad()
    {
        var (faulty, _) = TargetSeries("wash-000003");

        var qualities = string.Concat(faulty.Select(reading => reading.Quality switch { "uncertain" => 'u', "bad" => 'b', _ => '.' }));
        Assert.Contains("uuuuubbbbb", qualities, StringComparison.Ordinal);
        Assert.Equal(10, qualities.Count(quality => quality != '.'));
    }

    /// <summary>
    /// Once a day the oil probe reads below zero. The platform rejects the message that carries it,
    /// and its reason points at that reading: a value the contract does not allow.
    /// </summary>
    [Fact]
    public void OutOfRange_OneMessageADay_IsRejectedWithTheReadingNamed()
    {
        var history = FleetHistory.Create(Device("separator-000004"), FleetSchedule.Default, Now);

        var rejected = Assert.Single(
            history.Select(message => TelemetryValidator.Validate(Encoding.UTF8.GetString(message.Payload))),
            result => !result.IsValid);
        Assert.Matches(@"^/measurements/\d+/value: -\d+(\.\d+)? should be at least 0$", rejected.Error);
    }

    private static FleetDevice Device(string id) => FleetFile.Load(FleetFile.ReferencePath).Single(device => device.Id == id);

    /// <summary>The faulty sensor's readings from a device and from its healthy self, matched by time.</summary>
    private static (List<Reading> Faulty, List<Reading> Healthy) TargetSeries(string deviceId)
    {
        var device = Device(deviceId);
        var faulty = Readings(FleetHistory.Create(device, FleetSchedule.Default, Now));
        var healthy = Readings(FleetHistory.Create(device with { Faults = [] }, FleetSchedule.Default, Now));
        var target = Target(healthy);

        var faultySeries = faulty.Where(reading => reading.Series == target).ToList();
        var healthySeries = healthy.Where(reading => reading.Series == target && faultySeries.Any(other => other.MeasuredAt == reading.MeasuredAt)).ToList();
        Assert.Equal(faultySeries.Select(reading => reading.MeasuredAt), healthySeries.Select(reading => reading.MeasuredAt));
        return (faultySeries, healthySeries);
    }

    /// <summary>The faulty sensor: the device's first series of a level or a power.</summary>
    private static string Target(IEnumerable<Reading> readings) =>
        readings.First(reading => reading.Type is "level" or "power").Series;

    /// <summary>Every measurement of a history, read from the payloads as sent, valid or not.</summary>
    private static List<Reading> Readings(IEnumerable<FleetMessage> history) =>
        history.SelectMany(message =>
        {
            using var document = JsonDocument.Parse(message.Payload);
            return document.RootElement.GetProperty("measurements").EnumerateArray()
                .Select(measurement => new Reading(
                    measurement.GetProperty("type").GetString()!,
                    measurement.TryGetProperty("sensor", out var sensor) ? sensor.GetString() : null,
                    measurement.GetProperty("measuredAt").GetString()!,
                    measurement.GetProperty("value").GetDouble(),
                    measurement.TryGetProperty("quality", out var quality) ? quality.GetString()! : "ok"))
                .ToList();
        }).ToList();

    private static int LongestRun(IEnumerable<double> values)
    {
        int longest = 0, run = 0;
        double? previous = null;
        foreach (var value in values)
        {
            run = value == previous ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = value;
        }

        return longest;
    }

    private sealed record Reading(string Type, string? Sensor, string MeasuredAt, double Value, string Quality)
    {
        public string Series => Sensor is null ? Type : $"{Type}:{Sensor}";
    }
}
