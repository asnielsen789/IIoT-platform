using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IIoT.TestClient.Payloads;

namespace IIoT.TestClient.Fleet;

/// <summary>How a simulated device's history is laid out in time.</summary>
/// <param name="Window">How far back the history reaches.</param>
/// <param name="SampleEvery">How often the device measures.</param>
/// <param name="SendEvery">How often it transmits, with every sample taken since: a multiple of <paramref name="SampleEvery"/>.</param>
public sealed record FleetSchedule(TimeSpan Window, TimeSpan SampleEvery, TimeSpan SendEvery)
{
    /// <summary>A day, measured hourly and sent every four hours, as a battery-powered NB-IoT device would.</summary>
    public static readonly FleetSchedule Default = new(TimeSpan.FromHours(24), TimeSpan.FromHours(1), TimeSpan.FromHours(4));

    /// <summary>
    /// The most samples one message may carry. A car wash takes five measurements per sample, and the
    /// device contract allows 64 per message.
    /// </summary>
    public const int MaxSamplesPerMessage = 12;

    /// <summary>How many samples each message carries.</summary>
    public int SamplesPerMessage => (int)(SendEvery / SampleEvery);

    /// <summary>Why the schedule cannot be used, or <c>null</c>.</summary>
    public string? Problem()
    {
        if (SampleEvery < TimeSpan.FromMinutes(1))
        {
            return "Devices measure at most once a minute.";
        }

        if (SendEvery < SampleEvery || SendEvery.Ticks % SampleEvery.Ticks != 0)
        {
            return "The send interval must be a whole multiple of the sample interval.";
        }

        if (SamplesPerMessage > MaxSamplesPerMessage)
        {
            return $"A message may carry at most {MaxSamplesPerMessage} samples (64 measurements for a car wash); send more often or sample less often.";
        }

        return Window < SendEvery || Window.Ticks % SendEvery.Ticks != 0
            ? "The window must be a whole multiple of the send interval."
            : null;
    }
}

/// <summary>One message of a simulated history.</summary>
/// <param name="SentAt">When the device sent it, by its own clock.</param>
/// <param name="MeasurementCount">How many measurements it carries.</param>
/// <param name="Payload">The device contract's JSON, as the device publishes it.</param>
public sealed record FleetMessage(string DeviceId, string MessageId, DateTimeOffset SentAt, int MeasurementCount, byte[] Payload);

/// <summary>
/// A simulated device's recent history as the messages it would have sent: samples taken on a
/// schedule, buffered, and sent several at a time, each with its own <c>measuredAt</c> (ADR-004).
/// </summary>
/// <remarks>
/// <para>
/// Sent now, the history is backfill: the platform accepts it, records when it arrived, and keeps
/// each reading's own time. A dashboard then shows the device's last day, not one point.
/// </para>
/// <para>
/// The payloads are built from scratch as JSON, not from the platform's types, for the reason the
/// client replays fixtures as bytes: the client must not agree with the platform by sharing its
/// code. Values are deterministic for a device, its schedule and its end time; message IDs are new
/// on every run, so a history sent twice is two histories, not a replay.
/// </para>
/// </remarks>
public static class FleetHistory
{
    /// <param name="now">The end of the window; samples fall on whole multiples of the sample interval up to it.</param>
    public static IReadOnlyList<FleetMessage> Create(FleetDevice device, FleetSchedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(schedule));
        }

        var behaviour = DeviceBehaviour.For(device);
        var jitter = new Random(device.Seed ^ 0x5EED);

        var end = new DateTimeOffset(now.UtcTicks - now.UtcTicks % schedule.SampleEvery.Ticks, TimeSpan.Zero);
        var sampleCount = (int)(schedule.Window / schedule.SampleEvery);
        var first = end - schedule.Window + schedule.SampleEvery;

        var samples = Enumerable.Range(0, sampleCount)
            .Select(index => first + index * schedule.SampleEvery)
            .Select(at => (At: at.AddSeconds(jitter.Next(0, 9)), Readings: behaviour.Read(at, schedule.SampleEvery)))
            .ToList();

        var battery = 3.62 - jitter.NextDouble() * 0.15;
        var sequence = 1000 + device.Seed % 9000;

        return samples
            .Chunk(schedule.SamplesPerMessage)
            .Select((chunk, index) =>
            {
                var sentAt = chunk[^1].At.AddSeconds(12 + jitter.Next(0, 20));
                battery -= 0.0005;
                return Message(device, sentAt, sequence + index, chunk, battery, jitter);
            })
            .ToList();
    }

    private static FleetMessage Message(
        FleetDevice device,
        DateTimeOffset sentAt,
        long sequence,
        IReadOnlyList<(DateTimeOffset At, IReadOnlyList<Sample> Readings)> samples,
        double battery,
        Random jitter)
    {
        var measurements = new JsonArray();
        foreach (var (at, readings) in samples)
        {
            foreach (var reading in readings)
            {
                var measurement = new JsonObject
                {
                    ["type"] = reading.Type,
                    ["value"] = reading.Type == "count" ? JsonValue.Create((long)reading.Value) : JsonValue.Create(reading.Value),
                    ["unit"] = reading.Unit
                };

                if (reading.Sensor is not null)
                {
                    measurement["sensor"] = reading.Sensor;
                }

                measurement["measuredAt"] = Timestamp(at);

                // ok is the contract's default, so the device leaves it out, as real firmware saves bytes.
                if (reading.Quality != "ok")
                {
                    measurement["quality"] = reading.Quality;
                }

                measurements.Add(measurement);
            }
        }

        var metadata = new JsonObject { ["firmware"] = device.Firmware };
        if (device.OnBattery)
        {
            metadata["battery"] = new JsonObject { ["value"] = Math.Round(battery, 2), ["unit"] = "V" };
        }

        metadata["signal"] = new JsonObject { ["rsrp"] = jitter.Next(-125, -94), ["rsrq"] = jitter.Next(-18, -7) };

        var messageId = MessageId.New(sentAt);
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["messageId"] = messageId,
            ["deviceId"] = device.Id,
            ["sentAt"] = Timestamp(sentAt),
            ["sequence"] = sequence,
            ["measurements"] = measurements,
            ["device"] = metadata
        };

        return new FleetMessage(device.Id, messageId, sentAt, measurements.Count, JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
