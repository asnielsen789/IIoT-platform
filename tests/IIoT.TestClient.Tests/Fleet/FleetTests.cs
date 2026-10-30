using System.Text;
using System.Text.Json;
using IIoT.Core.Validation;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.Fleet;

namespace IIoT.TestClient.Tests.Fleet;

/// <summary>
/// The simulated fleet (#79): the reference fleet file, the devices' histories, the <c>fleet</c>
/// command line and a run that connects to nothing. Every history is checked against the
/// platform's own validator, as the replayed fixtures are, so the client and the platform agree
/// about the contract rather than about each other.
/// </summary>
public class FleetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 18, 23, 0, TimeSpan.Zero);

    public static TheoryData<string> ReferenceDevices()
    {
        var data = new TheoryData<string>();
        foreach (var device in FleetFile.Load(FleetFile.ReferencePath))
        {
            data.Add(device.Id);
        }

        return data;
    }

    [Fact]
    public void ReferenceFleet_IsTwentyDevicesOfFourTypes()
    {
        var fleet = FleetFile.Load(FleetFile.ReferencePath);

        Assert.Equal(20, fleet.Count);
        Assert.Equal(7, fleet.Count(device => device.Profile == DeviceProfile.Tank));
        Assert.Equal(5, fleet.Count(device => device.Profile == DeviceProfile.Charger));
        Assert.Equal(4, fleet.Count(device => device.Profile == DeviceProfile.CarWash));
        Assert.Equal(4, fleet.Count(device => device.Profile == DeviceProfile.OilSeparator));
        Assert.All(fleet, device => Assert.Null(FleetFile.IdProblem(device.Id)));
        Assert.All(fleet, device => Assert.False(string.IsNullOrWhiteSpace(device.Site)));
    }

    /// <summary>
    /// Every message of every device's day validates against the platform's contract, and is shaped
    /// as a buffered NB-IoT device sends it: six messages, each with four hours of samples in order,
    /// sent after the last of them, within the contract's 64 measurements.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReferenceDevices))]
    public void History_IsADayOfValidBufferedMessages(string deviceId)
    {
        var device = Device(deviceId);

        var history = FleetHistory.Create(device, FleetSchedule.Default, Now);

        Assert.Equal(6, history.Count);
        DateTimeOffset? previous = null;
        foreach (var message in history)
        {
            var result = TelemetryValidator.Validate(Encoding.UTF8.GetString(message.Payload));
            Assert.True(result.IsValid, $"{deviceId}: {result.Error}");

            var parsed = result.Message!;
            Assert.Equal(deviceId, parsed.DeviceId);
            Assert.Equal(message.MeasurementCount, parsed.Measurements.Count);
            Assert.InRange(parsed.Measurements.Count, 1, 64);

            var times = parsed.Measurements.Select(measurement => measurement.MeasuredAt).Distinct().ToList();
            Assert.Equal(4, times.Count);
            Assert.Equal(times.Order(), times);
            Assert.True(previous is null || times[0] > previous, "the samples run forwards across messages");
            Assert.True(parsed.SentAt > times[^1], "a message is sent after the samples it carries");
            Assert.InRange(times[0], Now.AddHours(-24), Now);
            previous = times[^1];
        }
    }

    /// <summary>The cumulative series only ever grow (ADR-011): no message lowers a meter.</summary>
    [Theory]
    [MemberData(nameof(ReferenceDevices))]
    public void History_CountersNeverGoDown(string deviceId)
    {
        var history = FleetHistory.Create(Device(deviceId), FleetSchedule.Default, Now);

        var series = history
            .SelectMany(message => TelemetryValidator.Validate(Encoding.UTF8.GetString(message.Payload)).Message!.Measurements)
            .Where(measurement => measurement.Type is "energy" or "volume" or "count")
            .GroupBy(measurement => (measurement.Type, measurement.Sensor));

        foreach (var meter in series)
        {
            var values = meter.Select(measurement => measurement.Value).ToList();
            Assert.Equal(values.Order(), values);
        }
    }

    [Theory]
    [InlineData("tank-000001", DeviceProfile.Tank, new[] { "level" })]
    [InlineData("charger-000001", DeviceProfile.Charger, new[] { "energy", "power", "count:sessions" })]
    [InlineData("wash-000001", DeviceProfile.CarWash, new[] { "volume:water", "count:cycles", "level:shampoo", "level:wax", "level:reclaim" })]
    [InlineData("separator-000001", DeviceProfile.OilSeparator, new[] { "level:oil", "level:sludge" })]
    public void EachProfile_SendsItsSeries(string deviceId, DeviceProfile profile, string[] expected)
    {
        var device = Device(deviceId);
        Assert.Equal(profile, device.Profile);

        var newest = TelemetryValidator.Validate(Encoding.UTF8.GetString(FleetHistory.Create(device, FleetSchedule.Default, Now)[^1].Payload)).Message!;

        var series = newest.Measurements
            .Select(measurement => measurement.Sensor is null ? measurement.Type : $"{measurement.Type}:{measurement.Sensor}")
            .Distinct();
        Assert.Equal(expected, series);
        Assert.Equal(device.OnBattery, newest.Device.Battery is not null);
    }

    /// <summary>
    /// A device behaves the same on every run and every machine: its seed comes from its ID by a
    /// fixed algorithm, not from <see cref="string.GetHashCode()"/>, which .NET randomises per process.
    /// </summary>
    [Fact]
    public void History_IsTheSameOnEveryRun_ApartFromMessageIds()
    {
        Assert.Equal(372015935, new FleetDevice("tank-000001", DeviceProfile.Tank, "").Seed);

        var first = FleetHistory.Create(Device("wash-000002"), FleetSchedule.Default, Now);
        var second = FleetHistory.Create(Device("wash-000002"), FleetSchedule.Default, Now);

        Assert.Equal(first.Select(Measurements), second.Select(Measurements));
        Assert.NotEqual(first[0].MessageId, second[0].MessageId);
    }

    [Theory]
    [InlineData(24 * 60, 0, 240, "at most once a minute")]
    [InlineData(24 * 60, 60, 90, "whole multiple of the sample interval")]
    [InlineData(24 * 60, 10, 240, "at most 12 samples")]
    [InlineData(300, 60, 240, "whole multiple of the send interval")]
    public void Schedule_ThatCannotBeSent_SaysWhy(int windowMinutes, int sampleMinutes, int sendMinutes, string reason)
    {
        var schedule = new FleetSchedule(TimeSpan.FromMinutes(windowMinutes), TimeSpan.FromMinutes(sampleMinutes), TimeSpan.FromMinutes(sendMinutes));

        Assert.Contains(reason, schedule.Problem(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "devices": [ { "id": "tank-1", "profile": "tank" }, { "id": "tank-1", "profile": "tank" } ] }""", "more than once")]
    [InlineData("""{ "devices": [ { "id": "tank@1", "profile": "tank" } ] }""", "may hold only")]
    [InlineData("""{ "devices": [ { "id": "tank-1.", "profile": "tank" } ] }""", "must end in")]
    [InlineData("""{ "devices": [ { "id": "tank-1", "profile": "boiler" } ] }""", "unknown profile 'boiler'")]
    [InlineData("""{ "devices": [ { "profile": "tank" } ] }""", "\"id\" is required")]
    [InlineData("""[ ]""", "\"devices\" array")]
    public void FleetFile_ThatIsNotAFleet_SaysWhyAndWhere(string json, string reason)
    {
        var exception = Assert.Throws<InvalidDataException>(() => FleetFile.Parse(json, "test.json"));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("test.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLine_Defaults_AndRepeatedDevices()
    {
        var options = CommandLine.ParseFleet(
            ["fleet", "--hub", "iot-x", "--device", "tank-000004", "--device", "wash-000001"],
            _ => null, "fleet.json", "secrets")!;

        Assert.Equal("iot-x.azure-devices.net", options.HostName);
        Assert.Equal("fleet.json", options.FleetFile);
        Assert.Equal("secrets", options.SecretsDirectory);
        Assert.Equal(FleetSchedule.Default, options.Schedule);
        Assert.Equal(["tank-000004", "wash-000001"], options.Devices);
        Assert.False(options.DryRun);
    }

    [Fact]
    public void CommandLine_ADryRun_NeedsNoHub_ButARunDoes()
    {
        Assert.True(CommandLine.ParseFleet(["fleet", "--dry-run"], _ => null, "f", "s")!.DryRun);
        Assert.Throws<CommandLineException>(() => CommandLine.ParseFleet(["fleet"], _ => null, "f", "s"));
        Assert.Equal("iot-y", CommandLine.ParseFleet(["fleet"], name => name == CommandLine.HubVariable ? "iot-y" : null, "f", "s")!.Hub);
    }

    [Theory]
    [InlineData("--cert", "c.pem")]
    [InlineData("--sample-every", "0")]
    public void CommandLine_WhatTheFleetDoesNotTake_IsRefused(string option, string value)
    {
        Assert.Throws<CommandLineException>(() => CommandLine.ParseFleet(["fleet", "--dry-run", option, value], _ => null, "f", "s"));
    }

    [Fact]
    public async Task DryRun_PrintsEachDevice_AndConnectsToNothing()
    {
        var log = new StringWriter();
        var options = new FleetOptions(null, FleetFile.ReferencePath, "unused", FleetSchedule.Default, ["charger-000002", "separator-000003"], DryRun: true);

        var failed = await new FleetRunner(options, log, () => Now).RunAsync(CancellationToken.None);

        Assert.Equal(0, failed);
        var output = log.ToString();
        Assert.Contains("[charger-000002] EV charger", output, StringComparison.Ordinal);
        Assert.Contains("[separator-000003] oil separator", output, StringComparison.Ordinal);
        Assert.DoesNotContain("tank-000001", output, StringComparison.Ordinal);
    }

    /// <summary>For scripts: the IDs, one a line, and nothing else; no hub needed.</summary>
    [Fact]
    public async Task List_PrintsTheIdsOnly()
    {
        var options = CommandLine.ParseFleet(["fleet", "--list", "--device", "tank-000004", "--device", "wash-000003"], _ => null, FleetFile.ReferencePath, "s")!;
        var log = new StringWriter();

        await new FleetRunner(options, log, () => Now).RunAsync(CancellationToken.None);

        Assert.Equal(["tank-000004", "wash-000003"], log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A device without a certificate and key is skipped and said so, not failed, and nothing connects.</summary>
    [Fact]
    public async Task Run_DevicesWithoutCredentials_AreSkipped()
    {
        var secrets = Directory.CreateTempSubdirectory("iiot-fleet-").FullName;
        try
        {
            var log = new StringWriter();
            var options = new FleetOptions("iot-unreachable.example", FleetFile.ReferencePath, secrets, FleetSchedule.Default, ["tank-000004"], DryRun: false);

            var failed = await new FleetRunner(options, log, () => Now).RunAsync(CancellationToken.None);

            Assert.Equal(0, failed);
            Assert.Contains("[tank-000004] skipped: no certificate and key", log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(secrets, recursive: true);
        }
    }

    [Fact]
    public async Task Run_AnUnknownDevice_IsRefused()
    {
        var options = new FleetOptions(null, FleetFile.ReferencePath, "unused", FleetSchedule.Default, ["tank-999999"], DryRun: true);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new FleetRunner(options, TextWriter.Null, () => Now).RunAsync(CancellationToken.None));
        Assert.Contains("'tank-999999'", exception.Message, StringComparison.Ordinal);
    }

    private static FleetDevice Device(string id) => FleetFile.Load(FleetFile.ReferencePath).Single(device => device.Id == id);

    private static string Measurements(FleetMessage message)
    {
        using var document = JsonDocument.Parse(message.Payload);
        return document.RootElement.GetProperty("measurements").GetRawText();
    }
}
