using System.Buffers;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace IIoT.TestClient.Provisioning;

/// <summary>
/// The three things the provisioning flow needs from an MQTT connection to DPS. Split out so the
/// flow (<see cref="DpsProvisioningClient"/>) can be tested against a scripted DPS, without a broker.
/// </summary>
public interface IDpsChannel
{
    /// <summary>Subscribes to <paramref name="topicFilter"/>.</summary>
    Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken);

    /// <summary>Publishes <paramref name="payload"/> to <paramref name="topic"/>.</summary>
    Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken);

    /// <summary>The next message DPS sends, in order of arrival.</summary>
    Task<DpsMessage> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>A message from DPS: its topic and its body.</summary>
public sealed record DpsMessage(string Topic, byte[] Payload);

/// <summary>
/// <see cref="IDpsChannel"/> over a connected MQTTnet client. QoS 1 both ways, the highest DPS
/// grants. Messages are queued as they arrive, so none is missed between a publish and the wait
/// for its answer.
/// </summary>
public sealed class MqttDpsChannel : IDpsChannel
{
    private readonly IMqttClient _client;
    private readonly Channel<DpsMessage> _received = Channel.CreateUnbounded<DpsMessage>();

    public MqttDpsChannel(IMqttClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _client.ApplicationMessageReceivedAsync += args =>
        {
            _received.Writer.TryWrite(new DpsMessage(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray()));
            return Task.CompletedTask;
        };
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken)
    {
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter, MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.SubscribeAsync(options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DpsMessage> ReceiveAsync(CancellationToken cancellationToken) =>
        await _received.Reader.ReadAsync(cancellationToken);
}
