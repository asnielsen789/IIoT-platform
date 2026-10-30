using System.Text;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.IotHub;
using MQTTnet;
using MQTTnet.Protocol;

namespace IIoT.TestClient.Fleet;

/// <summary>
/// <c>fleet</c>: each device of a fleet file connects to IoT Hub with its own certificate and sends
/// its simulated history, one device after another (#79).
/// </summary>
/// <remarks>
/// <para>
/// A device's certificate and key are found by its ID in the secrets directory, named as
/// <c>create-csr</c> and <c>IIoT.DeviceCa issue</c> write them: <c>&lt;id&gt;.chain.pem</c> (or
/// <c>.cert.pem</c>) and <c>&lt;id&gt;.key.pem</c>. A device without them is skipped and said so,
/// not failed: a fleet can be partly provisioned, and its other devices still send.
/// </para>
/// <para>
/// The connection is the ordinary one (<see cref="DeviceConnection"/>), as for a single device. The
/// hub must already know each device; provisioning it is a separate step.
/// </para>
/// </remarks>
public sealed class FleetRunner
{
    private readonly FleetOptions _options;
    private readonly TextWriter _log;
    private readonly Func<DateTimeOffset> _now;

    public FleetRunner(FleetOptions options, TextWriter log, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Sends every selected device's history.</summary>
    /// <returns>How many devices failed; skipped devices do not count.</returns>
    /// <exception cref="InvalidDataException">The fleet file is not valid, or selects no device.</exception>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var devices = Select(FleetFile.Load(_options.FleetFile));
        var now = _now();

        await _log.WriteLineAsync(
            $"{devices.Count} device(s), {_options.Schedule.Window.TotalHours:0.##} h of history: a sample every " +
            $"{_options.Schedule.SampleEvery.TotalMinutes:0} min, a message every {_options.Schedule.SendEvery.TotalMinutes:0} min" +
            (_options.DryRun ? "; dry run, nothing is sent" : $", to {_options.HostName}"));

        var failed = 0;
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = FleetHistory.Create(device, _options.Schedule, now);

            if (_options.DryRun)
            {
                await _log.WriteLineAsync(
                    $"[{device.Id}] {Name(device.Profile)}, {device.Site}: {messages.Count} message(s), " +
                    $"{messages.Sum(message => message.MeasurementCount)} measurement(s)");
                await _log.WriteLineAsync($"  newest: {Encoding.UTF8.GetString(messages[^1].Payload)}");
                continue;
            }

            if (!await SendAsync(device, messages, now, cancellationToken))
            {
                failed++;
            }
        }

        await _log.WriteLineAsync(failed == 0 ? "Done." : $"Done; {failed} device(s) failed.");
        return failed;
    }

    private IReadOnlyList<FleetDevice> Select(IReadOnlyList<FleetDevice> fleet)
    {
        if (_options.Devices.Count == 0)
        {
            return fleet;
        }

        var unknown = _options.Devices.Where(id => fleet.All(device => device.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException($"{_options.FleetFile} has no device {string.Join(", ", unknown.Select(id => $"'{id}'"))}.");
        }

        return fleet.Where(device => _options.Devices.Contains(device.Id)).ToList();
    }

    /// <returns>Whether the device sent everything, or was skipped.</returns>
    private async Task<bool> SendAsync(FleetDevice device, IReadOnlyList<FleetMessage> messages, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stem = DeviceKeyFiles.FileStem(device.Id);
        var certificate = new[] { ".chain.pem", ".cert.pem" }
            .Select(suffix => Path.Combine(_options.SecretsDirectory, stem + suffix))
            .FirstOrDefault(File.Exists);
        var key = Path.Combine(_options.SecretsDirectory, stem + ".key.pem");

        if (certificate is null || !File.Exists(key))
        {
            await _log.WriteLineAsync($"[{device.Id}] skipped: no certificate and key for it in {_options.SecretsDirectory}");
            return true;
        }

        try
        {
            using var authentication = X509CertificateAuthentication.Load(device.Id, certificate, key, now);
            using var client = new MqttClientFactory().CreateMqttClient();

            var connect = await client.ConnectAsync(DeviceConnection.Build(_options.HostName, authentication, now.AddHours(1)), cancellationToken);
            if (connect.ResultCode != MqttClientConnectResultCode.Success)
            {
                await _log.WriteLineAsync($"[{device.Id}] refused by the hub: {connect.ResultCode}");
                return false;
            }

            try
            {
                var sent = 0;
                foreach (var message in messages)
                {
                    var result = await client.PublishAsync(
                        new MqttApplicationMessageBuilder()
                            .WithTopic(DeviceTopic.Events(device.Id))
                            .WithPayload(message.Payload)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build(),
                        cancellationToken);

                    if (!result.IsSuccess)
                    {
                        await _log.WriteLineAsync($"[{device.Id}] message {message.MessageId} refused: {result.ReasonCode}");
                        return false;
                    }

                    sent++;
                }

                await _log.WriteLineAsync(
                    $"[{device.Id}] {Name(device.Profile)}: sent {sent} message(s), " +
                    $"{messages.Sum(message => message.MeasurementCount)} measurement(s)");
                return true;
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), shutdown.Token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The same terse report as a single device's run: the type and the message, never a key.
            await _log.WriteLineAsync($"[{device.Id}] failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string Name(DeviceProfile profile) => profile switch
    {
        DeviceProfile.Tank => "tank",
        DeviceProfile.Charger => "EV charger",
        DeviceProfile.CarWash => "car wash",
        _ => "oil separator"
    };
}
