using System.Text;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.IotHub;
using IIoT.TestClient.Payloads;
using MQTTnet;
using MQTTnet.Protocol;

namespace IIoT.TestClient.Simulation;

/// <summary>One simulated device: connect, publish on an interval, disconnect.</summary>
public sealed class DeviceSimulator
{
    private readonly IDeviceAuthentication _authentication;
    private readonly TestClientOptions _options;
    private readonly PublicationSequence _sequence;
    private readonly TextWriter _log;

    public DeviceSimulator(
        IDeviceAuthentication authentication,
        TestClientOptions options,
        PublicationSequence sequence,
        TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(log);

        _authentication = authentication;
        _options = options;
        _sequence = sequence;
        _log = log;
    }

    /// <summary>The device this instance simulates.</summary>
    public string DeviceId => _authentication.DeviceId;

    /// <summary>
    /// Runs until <c>--count</c> messages have been sent, or until
    /// <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            await DryRunAsync(cancellationToken);
            return;
        }

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        var clientOptions = DeviceConnection.Build(
            _options.HostName,
            _authentication,
            DateTimeOffset.UtcNow + _options.SasLifetime);

        var connect = await client.ConnectAsync(clientOptions, cancellationToken);
        await WriteAsync($"[{DeviceId}] connected: {connect.ResultCode}");

        try
        {
            await PublishLoopAsync(client, cancellationToken);
        }
        finally
        {
            if (client.IsConnected)
            {
                // Not the cancellation token: a cancelled run still deserves a clean DISCONNECT,
                // or IoT Hub reports the device as having dropped off the network.
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder().Build(), shutdown.Token);
            }
        }
    }

    private async Task PublishLoopAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        for (long index = 0; !Finished(index); index++)
        {
            if (index > 0 && _options.Interval > TimeSpan.Zero)
                await Task.Delay(_options.Interval, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var publication = _sequence.Next(DeviceId, index);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(publication.Topic)
                .WithPayload(publication.Payload)

                // QoS 1. QoS 2 makes IoT Hub close the connection, and QoS 0 would hide a
                // rejected publish, which is the opposite of what a test client is for.
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            var result = await client.PublishAsync(message, cancellationToken);
            await WriteAsync(
                $"[{DeviceId}] #{index + 1} {publication.FixtureName} " +
                $"{publication.Payload.Length}B -> {result.ReasonCode}");
        }
    }

    /// <summary>
    /// Prints what would be published, without a network connection. The reason this exists: the
    /// topic string in the contract has never been verified against a real hub, so being able to
    /// read it back exactly — property bag and all — is useful before there is a hub at all.
    /// </summary>
    private async Task DryRunAsync(CancellationToken cancellationToken)
    {
        await WriteAsync($"[{DeviceId}] dry run against {_options.HostName}:{DeviceConnection.Port}");
        await WriteAsync($"[{DeviceId}] username: {DeviceConnection.UserName(_options.HostName, DeviceId)}");

        for (long index = 0; !Finished(index); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var publication = _sequence.Next(DeviceId, index);
            await WriteAsync($"[{DeviceId}] #{index + 1} {publication.FixtureName}");
            await WriteAsync($"  topic:   {publication.Topic}");
            await WriteAsync($"  payload: {Encoding.UTF8.GetString(publication.Payload)}");

            // One pass over the selection is enough to inspect; an unbounded dry run would only
            // repeat itself.
            if (_options.MessageCount is null && index + 1 >= _sequence.Count)
                break;
        }
    }

    private bool Finished(long index) =>
        _options.MessageCount is { } limit && index >= limit;

    private Task WriteAsync(string line) => _log.WriteLineAsync(line);
}
