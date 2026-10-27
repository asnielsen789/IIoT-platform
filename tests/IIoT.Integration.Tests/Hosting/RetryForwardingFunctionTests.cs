using System.Net;
using System.Text.Json;
using Azure;
using IIoT.Core.DeadLettering;
using IIoT.Core.Forwarders;
using IIoT.Core.Services;
using IIoT.Functions;
using IIoT.Functions.DeadLettering;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IIoT.Integration.Tests.Hosting;

/// <summary>
/// What <see cref="RetryForwardingFunction"/> does with each kind of entry and outcome, with a
/// forwarder that fails on demand. Every entry ends delivered, back on its queue through an
/// exception, or in the poison queue — never dropped, and never dead-lettered a second time.
/// </summary>
public class RetryForwardingFunctionTests
{
    private const string AuthenticatedDevice = "tank-000123";
    private const string MessageId = "01J8F2K9QS4T7V0X3Z6B8N1M5B";

    private static readonly DateTimeOffset EnqueuedAt = new(2026, 10, 15, 12, 42, 57, 954, TimeSpan.Zero);
    private static readonly DateTimeOffset DeadLetteredAt = new(2026, 10, 15, 12, 43, 29, 0, TimeSpan.Zero);

    private readonly ScriptedForwarder _forwarder = new();
    private readonly RecordingDeadLetterQueue _queue = new();
    private readonly CapturingLoggerProvider _logs = new();

    /// <summary>
    /// A retry is the first attempt over again: the forwarder is handed the stored identity and the
    /// stored enqueued time, never anything from the moment of the retry, and the message whose id is
    /// the Idempotency-Key. Delivered, the entry is left for the runtime to delete; nothing is written.
    /// </summary>
    [Fact]
    public async Task Delivered_NothingIsWritten_AndTheForwarderGetsTheStoredIdentityTimeAndMessage()
    {
        await Function().RetryForwarding(Entry(), TestFunctionContext.ForQueueAttempt(3));

        var (message, context) = Assert.Single(_forwarder.Calls);
        Assert.Equal(MessageId, message.MessageId);
        Assert.Equal(AuthenticatedDevice, context.AuthenticatedDeviceId);
        Assert.Equal(EnqueuedAt, context.EnqueuedAt);

        Assert.Empty(_queue.Added);
        Assert.Empty(_queue.Poisoned);

        var delivered = Assert.Single(_logs.For<RetryForwardingFunction>(), entry => entry.Message.StartsWith("Delivered", StringComparison.Ordinal));
        Assert.Equal("3", delivered.Properties["Attempt"]);
        Assert.Equal(MessageId, delivered.Properties["MessageId"]);
    }

    /// <summary>
    /// Forwarding fails again with attempts left: the function throws, so the queue trigger keeps the
    /// entry and tries it again after the retry interval. It is not dead-lettered again — nothing is
    /// written anywhere — so a message cannot loop between the two queues.
    /// </summary>
    [Fact]
    public async Task ForwardingFails_WithAttemptsLeft_Throws_AndWritesNothing()
    {
        _forwarder.FailWith = new ForwardingException("Gateway v1 responded 503 ServiceUnavailable") { StatusCode = HttpStatusCode.ServiceUnavailable };

        var thrown = await Assert.ThrowsAsync<ForwardingRetryException>(
            () => Function().RetryForwarding(Entry(), TestFunctionContext.ForQueueAttempt(2)));

        Assert.Contains(MessageId, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_queue.Added);
        Assert.Empty(_queue.Poisoned);

        var warning = Assert.Single(_logs.For<RetryForwardingFunction>(), entry => entry.Level == LogLevel.Warning);
        Assert.Equal("2", warning.Properties["Attempt"]);
        Assert.Equal(ForwardingRetrySchedule.DefaultMaxAttempts, warning.Properties["MaxAttempts"]);
    }

    /// <summary>
    /// On the last attempt the entry goes to the poison queue — unchanged, so an operator can put it
    /// back as it is — and the function returns, so the runtime deletes it from the retry queue
    /// rather than moving it itself with an expiry.
    /// </summary>
    [Fact]
    public async Task ForwardingFails_OnTheLastAttempt_MovesTheEntryUnchangedToPoison()
    {
        _forwarder.FailWith = new HttpRequestException("Connection refused (gateway.iiot.test:443)");
        var entry = Entry();

        await Function().RetryForwarding(entry, TestFunctionContext.ForQueueAttempt(ForwardingRetrySchedule.DefaultMaxAttempts));

        Assert.Equal(entry, Assert.Single(_queue.Poisoned));
        Assert.Empty(_queue.Added);

        var moved = Assert.Single(_logs.For<RetryForwardingFunction>(), log => log.Message.StartsWith("Moved", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, moved.Level);
        Assert.Contains("last of 8 attempts: Connection refused", (string)moved.Properties["Reason"]!, StringComparison.Ordinal);
    }

    /// <summary>An app setting that overrides host.json's attempt limit moves the last attempt with it.</summary>
    [Fact]
    public async Task OverriddenAttemptLimit_IsTheLastAttempt()
    {
        _forwarder.FailWith = new HttpRequestException("Connection refused");
        var schedule = ForwardingRetrySchedule.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureFunctionsJobHost:extensions:queues:maxDequeueCount"] = "3" })
            .Build());

        await Assert.ThrowsAsync<ForwardingRetryException>(() => Function(schedule).RetryForwarding(Entry(), TestFunctionContext.ForQueueAttempt(2)));
        await Function(schedule).RetryForwarding(Entry(), TestFunctionContext.ForQueueAttempt(3));

        Assert.Single(_queue.Poisoned);
    }

    /// <summary>
    /// The worker hands trigger metadata over as text or JSON rather than as a number. The last
    /// attempt is recognised in whichever form the dequeue count arrives.
    /// </summary>
    [Theory]
    [MemberData(nameof(DequeueCountForms))]
    public async Task DequeueCount_IsReadInEveryFormTheWorkerMayUse(object dequeueCount)
    {
        _forwarder.FailWith = new HttpRequestException("Connection refused");
        var context = new TestFunctionContext(new Dictionary<string, object?> { ["dequeueCount"] = dequeueCount });

        await Function().RetryForwarding(Entry(), context);

        Assert.Single(_queue.Poisoned);
    }

    public static TheoryData<object> DequeueCountForms => new()
    {
        "8",
        "\"8\"",
        8L,
        8,
        JsonDocument.Parse("8").RootElement.Clone()
    };

    /// <summary>
    /// Without a dequeue count the function cannot know the last attempt, so it leaves the limit to
    /// the runtime: it throws, and the runtime moves the entry to the poison queue when the limit is
    /// reached.
    /// </summary>
    [Fact]
    public async Task DequeueCountUnknown_ForwardingFails_ThrowsAndLeavesTheLimitToTheRuntime()
    {
        _forwarder.FailWith = new HttpRequestException("Connection refused");

        await Assert.ThrowsAsync<ForwardingRetryException>(() => Function().RetryForwarding(Entry(), new TestFunctionContext()));

        Assert.Empty(_queue.Poisoned);
    }

    public static TheoryData<string, string, ProcessingOutcome> PermanentlyRejected => new()
    {
        // The contract the platform enforces changed since the entry was written.
        { "invalid-wrong-unit.json", AuthenticatedDevice, ProcessingOutcome.RejectedInvalidPayload },
        // The payload names another device than the stored authenticated identity.
        { "valid-full.json", "tank-000999", ProcessingOutcome.RejectedIdentityMismatch }
    };

    /// <summary>
    /// Re-validation refuses the message. Another attempt would end the same way, so the entry goes
    /// straight to the poison queue, on the first attempt, without the forwarder being called — never
    /// dropped, and with the outcome in the reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(PermanentlyRejected))]
    public async Task RejectedOnRetry_GoesStraightToPoison(string fixture, string storedDeviceId, ProcessingOutcome outcome)
    {
        var entry = Entry(ContractFixtures.ReadDevice(fixture), storedDeviceId);

        await Function().RetryForwarding(entry, TestFunctionContext.ForQueueAttempt(1));

        Assert.Empty(_forwarder.Calls);
        Assert.Equal(entry, Assert.Single(_queue.Poisoned));

        var moved = Assert.Single(_logs.For<RetryForwardingFunction>(), log => log.Message.StartsWith("Moved", StringComparison.Ordinal));
        Assert.Contains(outcome.ToString(), (string)moved.Properties["Reason"]!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry this build cannot read — malformed, or written by a build with a later envelope
    /// version — goes to the poison queue unchanged, rather than failing on every attempt until the
    /// runtime gives up on it.
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"envelopeVersion":2,"messageId":"x"}""")]
    [InlineData("""{"envelopeVersion":1,"messageId":"x"}""")]
    public async Task UnreadableEntry_GoesStraightToPoison_Unchanged(string entry)
    {
        await Function().RetryForwarding(entry, TestFunctionContext.ForQueueAttempt(1));

        Assert.Empty(_forwarder.Calls);
        Assert.Equal(entry, Assert.Single(_queue.Poisoned));

        var moved = Assert.Single(_logs.For<RetryForwardingFunction>());
        Assert.Contains("the entry cannot be read", (string)moved.Properties["Reason"]!, StringComparison.Ordinal);
    }

    /// <summary>
    /// If the move to the poison queue fails, the function throws, and the entry stays on its queue to
    /// be tried again. Returning would let the runtime delete an entry that is nowhere else.
    /// </summary>
    [Fact]
    public async Task MoveToPoisonFails_Throws_SoTheEntryIsKept()
    {
        _queue.PoisonFailsWith = new RequestFailedException(503, "The server is busy.");

        await Assert.ThrowsAsync<ForwardingRetryException>(
            () => Function().RetryForwarding("not json at all", TestFunctionContext.ForQueueAttempt(1)));

        Assert.Empty(_queue.Poisoned);
        Assert.Single(_logs.For<RetryForwardingFunction>(), entry => entry.Message.StartsWith("Could not move", StringComparison.Ordinal));
    }

    private RetryForwardingFunction Function(ForwardingRetrySchedule? schedule = null)
    {
        var loggers = new LoggerFactory([_logs]);
        return new RetryForwardingFunction(
            new TelemetryService(_forwarder, loggers.CreateLogger<TelemetryService>()),
            _queue,
            schedule ?? ForwardingRetrySchedule.FromConfiguration(new ConfigurationBuilder().Build()),
            loggers.CreateLogger<RetryForwardingFunction>());
    }

    private static string Entry(string? payload = null, string deviceId = AuthenticatedDevice) =>
        DeadLetterEnvelopeJson.Serialize(DeadLetterEnvelope.Create(
            payload ?? ContractFixtures.ReadDevice("valid-full.json"),
            deviceId,
            EnqueuedAt,
            MessageId,
            "Gateway v1 responded 503 ServiceUnavailable",
            503,
            DeadLetteredAt));
}
