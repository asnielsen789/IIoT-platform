using System.Text;
using System.Text.Json;
using IIoT.TestClient.Provisioning;

namespace IIoT.TestClient.Tests.Provisioning;

/// <summary>
/// The register-and-poll flow against a scripted DPS: which requests go out, which answers count,
/// and how long the client waits between them.
/// </summary>
public class DpsProvisioningClientTests
{
    private const string RegistrationId = "tank-000003";
    private const string OperationId = "4.d0a671905ea5b2c8.42d78160-4c78-479e-8be7-61d5e55dac0d";
    private const string Hub = "iot-example.azure-devices.net";

    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task Provision_RegistersThenPollsUntilAssigned()
    {
        var channel = new ScriptedDpsChannel(
            [Assigning("1", retryAfter: 3)],
            [Assigned("2")]);

        var outcome = await ProvisionAsync(channel);

        Assert.Equal(new DpsAssigned(Hub, RegistrationId, "initialAssignment"), outcome);
        Assert.Equal([DpsTopics.ResponseFilter], channel.Subscriptions);
        Assert.Equal([DpsTopics.Register("1"), DpsTopics.OperationStatus("2", OperationId)], channel.Topics);
        Assert.Equal(RegistrationId, JsonDocument.Parse(channel.Payloads[0]).RootElement.GetProperty("registrationId").GetString());
        Assert.Empty(channel.Payloads[1]);
        Assert.Equal([TimeSpan.FromSeconds(3)], _delays);
    }

    [Fact]
    public async Task Provision_AnImmediateAssignment_NeedsNoPoll()
    {
        var channel = new ScriptedDpsChannel([Assigned("1")]);

        var outcome = await ProvisionAsync(channel);

        Assert.IsType<DpsAssigned>(outcome);
        Assert.Single(channel.Topics);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Provision_AThrottledRegistration_IsSentAgainAfterTheWait()
    {
        var channel = new ScriptedDpsChannel(
            [Response("1", 429, 10, """{"errorCode":429001,"message":"Throttled."}""")],
            [Assigning("2", retryAfter: 3)],
            [Assigned("3")]);

        var outcome = await ProvisionAsync(channel);

        Assert.IsType<DpsAssigned>(outcome);
        Assert.Equal(
            [DpsTopics.Register("1"), DpsTopics.Register("2"), DpsTopics.OperationStatus("3", OperationId)],
            channel.Topics);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)], _delays);
    }

    [Fact]
    public async Task Provision_AThrottledPoll_PollsTheSameOperationAgain()
    {
        var channel = new ScriptedDpsChannel(
            [Assigning("1", retryAfter: 3)],
            [Response("2", 429, 5, """{"errorCode":429001,"message":"Throttled."}""")],
            [Assigned("3")]);

        await ProvisionAsync(channel);

        Assert.Equal(DpsTopics.OperationStatus("3", OperationId), channel.Topics[2]);
    }

    [Fact]
    public async Task Provision_ARefusal_EndsTheRunWithDpsReason()
    {
        var channel = new ScriptedDpsChannel(
            [Response("1", 401, null, """{"errorCode":401002,"message":"The device is unauthorized."}""")]);

        var outcome = await ProvisionAsync(channel);

        Assert.Equal(new DpsFailed(401, 401002, "The device is unauthorized."), outcome);
        Assert.Single(channel.Topics);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Provision_OnlyTheAnswerToTheCurrentRequestCounts()
    {
        // A late answer to an earlier request and a message that is no DPS response at all arrive
        // first. Taking either for the answer would assign the device to the wrong hub.
        var channel = new ScriptedDpsChannel(
            [
                new DpsMessage("devices/tank-000003/messages/devicebound/", [1, 2, 3]),
                Response("0", 200, null, AssignedBody("iot-stale.azure-devices.net")),
                Assigning("1", retryAfter: 3)
            ],
            [
                Response("1", 200, null, AssignedBody("iot-stale.azure-devices.net")),
                Assigned("2")
            ]);

        var outcome = await ProvisionAsync(channel);

        Assert.Equal(Hub, Assert.IsType<DpsAssigned>(outcome).AssignedHub);
    }

    [Fact]
    public async Task Provision_NeverAssigned_StopsAfterTheLastRequest()
    {
        var channel = new ScriptedDpsChannel(
            [Assigning("1", retryAfter: 3)],
            [Assigning("2", retryAfter: 3)],
            [Assigning("3", retryAfter: 3)]);

        var outcome = await ProvisionAsync(channel, maxRequests: 3);

        var failed = Assert.IsType<DpsFailed>(outcome);
        Assert.Contains("after 3 requests", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(3, channel.Topics.Count);
    }

    [Fact]
    public async Task Provision_ACancelledWait_StopsTheRun()
    {
        using var cancellation = new CancellationTokenSource();
        var channel = new ScriptedDpsChannel([Assigning("1", retryAfter: 3)], [Assigned("2")]);
        var client = new DpsProvisioningClient(
            channel,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            },
            TextWriter.Null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ProvisionAsync(RegistrationId, 10, cancellation.Token));
        Assert.Single(channel.Topics);
    }

    private Task<DpsOutcome> ProvisionAsync(ScriptedDpsChannel channel, int maxRequests = 10)
    {
        var client = new DpsProvisioningClient(
            channel,
            (wait, _) =>
            {
                _delays.Add(wait);
                return Task.CompletedTask;
            },
            TextWriter.Null);

        return client.ProvisionAsync(RegistrationId, maxRequests, CancellationToken.None);
    }

    private static DpsMessage Assigning(string requestId, int retryAfter) =>
        Response(requestId, 202, retryAfter, $$"""{"operationId":"{{OperationId}}","status":"assigning"}""");

    private static DpsMessage Assigned(string requestId) => Response(requestId, 200, null, AssignedBody(Hub));

    private static string AssignedBody(string hub) => $$$"""
        {"operationId":"{{{OperationId}}}","status":"assigned",
         "registrationState":{"registrationId":"{{{RegistrationId}}}","assignedHub":"{{{hub}}}",
          "deviceId":"{{{RegistrationId}}}","status":"assigned","substatus":"initialAssignment"}}
        """;

    private static DpsMessage Response(string requestId, int status, int? retryAfter, string body) =>
        new(
            $"$dps/registrations/res/{status}/?$rid={requestId}" + (retryAfter is { } s ? $"&retry-after={s}" : ""),
            Encoding.UTF8.GetBytes(body));

    /// <summary>
    /// A DPS that answers each publish, in order, with the next script entry. Anything received
    /// beyond the script fails the test rather than hanging it.
    /// </summary>
    private sealed class ScriptedDpsChannel(params DpsMessage[][] answers) : IDpsChannel
    {
        private readonly Queue<DpsMessage[]> _answers = new(answers);
        private readonly Queue<DpsMessage> _inbox = new();

        public List<string> Subscriptions { get; } = [];

        public List<string> Topics { get; } = [];

        public List<byte[]> Payloads { get; } = [];

        public Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken)
        {
            Subscriptions.Add(topicFilter);
            return Task.CompletedTask;
        }

        public Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(Subscriptions);

            Topics.Add(topic);
            Payloads.Add(payload);
            foreach (var answer in _answers.Count > 0 ? _answers.Dequeue() : [])
                _inbox.Enqueue(answer);

            return Task.CompletedTask;
        }

        public Task<DpsMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            _inbox.Count > 0
                ? Task.FromResult(_inbox.Dequeue())
                : throw new InvalidOperationException("The client waited for an answer the script does not have.");
    }
}
