using System.Net;
using IIoT.Core.DeadLettering;
using IIoT.Functions.DeadLettering;
using IIoT.Integration.Tests.Infrastructure;

namespace IIoT.Integration.Tests.Forwarding;

/// <summary>
/// Dead-letter handling end to end, in-process: the IoT Hub function, the real forwarding pipeline
/// with real RS256 JWTs, the gateway mock, and the retry function, composed as the worker composes
/// them. Only the Storage queues are a list (<see cref="RecordingDeadLetterQueue"/>).
/// </summary>
/// <remarks>
/// The property that matters is that a retry cannot store a batch twice. Every attempt — the first
/// one's own retries and every later retry — sends the same batch with the same Idempotency-Key,
/// so a gateway that stored it on an attempt whose answer was lost acknowledges the retry as a
/// replay.
/// </remarks>
public class DeadLetterRetryTests : IAsyncLifetime
{
    private const string AuthenticatedDevice = "tank-000123";
    private const string MessageId = "01J8F2K9QS4T7V0X3Z6B8N1M5B";

    /// <summary>The first attempt plus the standard resilience pipeline's three retries.</summary>
    private const int AttemptsPerPass = 4;

    private static readonly DateTimeOffset EnqueuedAt = new(2026, 10, 15, 12, 42, 57, 954, TimeSpan.Zero);

    private readonly TestTokenIssuer _issuer = new();
    private readonly GatewayMockFactory _gateway;
    private readonly ForwardingPipeline _pipeline;

    public DeadLetterRetryTests()
    {
        _gateway = new GatewayMockFactory(_issuer.GatewaySettings());
        _pipeline = new ForwardingPipeline(_gateway, _issuer);
    }

    /// <summary>
    /// The gateway is down for the whole first attempt, so the message is dead-lettered; it is back
    /// when the retry runs, which delivers the batch under the identity and with the receipt time of
    /// the original delivery. Stored once.
    /// </summary>
    [Fact]
    public async Task GatewayDown_TheMessageIsDeadLettered_AndTheRetryDeliversIt_Once()
    {
        _pipeline.Traffic.Fault = attempt => attempt <= AttemptsPerPass ? HttpStatusCode.ServiceUnavailable : null;

        await _pipeline.ProcessTelemetryAsync(Event());

        Assert.Empty(_gateway.Store.Batches);
        var (entry, _) = Assert.Single(_pipeline.DeadLetters.Added);
        var envelope = DeadLetterEnvelopeJson.Read(entry).Envelope!;
        Assert.Equal(503, envelope.Failure.StatusCode);

        await _pipeline.RetryForwardingAsync(entry, attempt: 1);

        var stored = Assert.Single(_gateway.Store.Batches);
        Assert.Equal(AuthenticatedDevice, stored.DeviceId);
        Assert.Equal(MessageId, stored.IdempotencyKey);
        Assert.Equal(TestTokenIssuer.Subject, stored.Caller);
        Assert.Equal(EnqueuedAt, stored.Body.GetProperty("receivedAt").GetDateTimeOffset());

        Assert.Single(_pipeline.DeadLetters.Added);
        Assert.Empty(_pipeline.DeadLetters.Poisoned);
    }

    /// <summary>
    /// The case idempotency exists for. The first attempt did reach the gateway, which stored the
    /// batch, but its answer was lost on the way back — every time — so the platform dead-lettered a
    /// message the gateway already had. The retry is acknowledged as a replay, and the gateway still
    /// holds exactly one batch.
    /// </summary>
    [Fact]
    public async Task FirstAttemptReachedTheGateway_ButItsAnswerWasLost_TheBatchIsStillStoredOnce()
    {
        _pipeline.Traffic.LoseResponse = attempt => attempt <= AttemptsPerPass ? HttpStatusCode.BadGateway : null;

        await _pipeline.ProcessTelemetryAsync(Event());

        Assert.Single(_gateway.Store.Batches);
        var (entry, _) = Assert.Single(_pipeline.DeadLetters.Added);

        await _pipeline.RetryForwardingAsync(entry, attempt: 1);

        Assert.Single(_gateway.Store.Batches);
        Assert.Equal(Enumerable.Repeat(HttpStatusCode.BadGateway, AttemptsPerPass).Append(HttpStatusCode.Accepted), _pipeline.Traffic.Responses);
        Assert.All(_pipeline.Traffic.IdempotencyKeys, key => Assert.Equal(MessageId, key));
        Assert.Empty(_pipeline.DeadLetters.Poisoned);
    }

    /// <summary>
    /// A gateway that stays down through every attempt: each retry but the last fails and throws, so
    /// the queue keeps the entry, and the last moves it to the poison queue unchanged. Nothing is
    /// stored, nothing is dead-lettered twice, and the entry is never lost.
    /// </summary>
    [Fact]
    public async Task GatewayStaysDown_EveryRetryFails_AndTheLastMovesTheEntryToPoison()
    {
        _pipeline.Traffic.Fault = _ => HttpStatusCode.ServiceUnavailable;

        await _pipeline.ProcessTelemetryAsync(Event());
        var (entry, _) = Assert.Single(_pipeline.DeadLetters.Added);

        for (var attempt = 1; attempt < ForwardingRetrySchedule.DefaultMaxAttempts; attempt++)
        {
            await Assert.ThrowsAsync<ForwardingRetryException>(() => _pipeline.RetryForwardingAsync(entry, attempt));
        }

        await _pipeline.RetryForwardingAsync(entry, ForwardingRetrySchedule.DefaultMaxAttempts);

        Assert.Equal(entry, Assert.Single(_pipeline.DeadLetters.Poisoned));
        Assert.Single(_pipeline.DeadLetters.Added);
        Assert.Empty(_gateway.Store.Batches);
        Assert.Equal((1 + ForwardingRetrySchedule.DefaultMaxAttempts) * AttemptsPerPass, _pipeline.Traffic.Attempts);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        await _gateway.DisposeAsync();
        _issuer.Dispose();
    }

    private static Azure.Messaging.EventHubs.EventData Event() =>
        IoTHubEvents.AsTheWorkerReceivesIt(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);
}
