using System.Globalization;

namespace IIoT.TestClient.Provisioning;

/// <summary>
/// Registers a device with DPS and waits for its assignment: subscribe to the responses, send the
/// registration, then poll the operation until DPS assigns the device or refuses it (#48).
/// </summary>
/// <remarks>
/// <para>
/// Every request carries a new <c>$rid</c>, and only the answer with that <c>$rid</c> counts. An
/// answer to an earlier request, or a message that is not a DPS response at all, is skipped, so a
/// late reply cannot be mistaken for the current one.
/// </para>
/// <para>
/// The waits come from DPS: <c>retry-after</c> between polls, and again after a throttled request,
/// which is then sent once more. The number of requests is bounded, and so is the whole run, by the
/// caller's cancellation token: a device that is never assigned stops with a reason instead of
/// polling forever.
/// </para>
/// </remarks>
public sealed class DpsProvisioningClient
{
    private readonly IDpsChannel _channel;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TextWriter _log;
    private int _requestCount;

    /// <param name="channel">The MQTT connection to DPS, already connected.</param>
    /// <param name="delay">Waits the given time. Injected so tests run without real waits.</param>
    /// <param name="log">Where progress is written.</param>
    public DpsProvisioningClient(IDpsChannel channel, Func<TimeSpan, CancellationToken, Task> delay, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(log);

        _channel = channel;
        _delay = delay;
        _log = log;
    }

    /// <summary>
    /// Registers <paramref name="registrationId"/> and returns DPS's final word: <see cref="DpsAssigned"/>
    /// or <see cref="DpsFailed"/>.
    /// </summary>
    /// <param name="registrationId">The device certificate's CN.</param>
    /// <param name="maxRequests">The most requests to send, the registration and every poll and retry included.</param>
    /// <param name="cancellationToken">Bounds the whole run.</param>
    public async Task<DpsOutcome> ProvisionAsync(string registrationId, int maxRequests, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequests, 1);

        await _channel.SubscribeAsync(DpsTopics.ResponseFilter, cancellationToken);

        string? operationId = null;
        for (var request = 1; request <= maxRequests; request++)
        {
            var requestId = (++_requestCount).ToString(CultureInfo.InvariantCulture);
            if (operationId is null)
            {
                await _channel.PublishAsync(
                    DpsTopics.Register(requestId), DpsRegistration.RequestPayload(registrationId), cancellationToken);
            }
            else
            {
                await _channel.PublishAsync(DpsTopics.OperationStatus(requestId, operationId), [], cancellationToken);
            }

            var (topic, body) = await ReceiveAnswerAsync(requestId, cancellationToken);
            switch (DpsRegistration.Read(topic, body))
            {
                case DpsInProgress inProgress:
                    operationId = inProgress.OperationId;
                    await _log.WriteLineAsync($"[{registrationId}] DPS: assigning, asking again in {inProgress.RetryAfter.TotalSeconds:0} s");
                    await _delay(inProgress.RetryAfter, cancellationToken);
                    break;

                case DpsThrottled throttled:
                    await _log.WriteLineAsync($"[{registrationId}] DPS: throttled, trying again in {throttled.RetryAfter.TotalSeconds:0} s");
                    await _delay(throttled.RetryAfter, cancellationToken);
                    break;

                case var final:
                    return final;
            }
        }

        return new DpsFailed(0, null, $"DPS had not assigned the device after {maxRequests} requests.");
    }

    private async Task<(DpsResponseTopic Topic, byte[] Body)> ReceiveAnswerAsync(string requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await _channel.ReceiveAsync(cancellationToken);
            if (DpsTopics.TryParseResponse(message.Topic, out var response) && response.RequestId == requestId)
                return (response, message.Payload);
        }
    }
}
