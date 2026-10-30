using System.Net;
using System.Text.Json;
using IIoT.Core.Authentication;
using IIoT.Core.Interfaces;
using IIoT.Core.Services;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.Forwarding;

/// <summary>
/// The real pipeline — <see cref="TelemetryService"/>, the v1 forwarder, the token provider and
/// the resilience handler, registered exactly as the Function registers them — delivering to the
/// real gateway mock, which validates the JWT and the body on every request. In-process: no
/// socket is opened.
/// </summary>
/// <remarks>
/// Each test gets its own issuer, mock and pipeline, so what one test leaves in the store cannot
/// make another pass.
/// </remarks>
public class ForwardingPipelineTests : IAsyncLifetime
{
    private const string AuthenticatedDevice = "tank-000123";
    private static readonly DateTimeOffset EnqueuedAt = new(2026, 9, 14, 6, 0, 13, 250, TimeSpan.Zero);

    private readonly TestTokenIssuer _issuer = new();
    private readonly GatewayMockFactory _gateway;
    private ForwardingPipeline _pipeline;

    public ForwardingPipelineTests()
    {
        _gateway = new GatewayMockFactory(_issuer.GatewaySettings());
        _pipeline = new ForwardingPipeline(_gateway, _issuer);
    }

    /// <summary>
    /// The demo criterion of the iteration, in-process: a valid message arrives at the gateway
    /// transformed into the v1 contract, delivered with a JWT the gateway accepted.
    /// </summary>
    [Fact]
    public async Task ValidMessage_ArrivesTransformed_WithAValidJwt()
    {
        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);

        var stored = Assert.Single(_gateway.Store.Batches);
        Assert.Equal(TestTokenIssuer.Subject, stored.Caller);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", stored.IdempotencyKey);

        var body = stored.Body;
        Assert.Equal(1, body.GetProperty("contractVersion").GetInt32());
        Assert.Equal(AuthenticatedDevice, body.GetProperty("deviceId").GetString());
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", body.GetProperty("messageId").GetString());
        Assert.Equal(EnqueuedAt, body.GetProperty("receivedAt").GetDateTimeOffset());
        Assert.Equal("1.4.2", body.GetProperty("firmware").GetString());

        var reading = Assert.Single(body.GetProperty("readings").EnumerateArray());
        Assert.Equal("level", reading.GetProperty("kind").GetString());
        Assert.Equal(1432.5, reading.GetProperty("value").GetDouble());
        Assert.Equal("mm", reading.GetProperty("unit").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T06:00:10Z"), reading.GetProperty("at").GetDateTimeOffset());
        Assert.Equal("ok", reading.GetProperty("quality").GetString());

        Assert.False(body.TryGetProperty("device", out _), "device metadata beyond firmware must not be forwarded");
    }

    /// <summary>
    /// A payload without deviceId still arrives with one: the identity IoT Hub authenticated.
    /// </summary>
    [Fact]
    public async Task MessageWithoutDeviceId_ArrivesUnderTheAuthenticatedIdentity()
    {
        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-minimal.json"), "tank-000777", EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        var stored = Assert.Single(_gateway.Store.Batches);
        Assert.Equal("tank-000777", stored.DeviceId);
        Assert.Equal("tank-000777", stored.Body.GetProperty("deviceId").GetString());
    }

    /// <summary>
    /// Buffered backfill is one request carrying every reading, in order, each with its own
    /// measurement time — never split into one call per reading (ADR-004).
    /// </summary>
    [Fact]
    public async Task BufferedBackfill_ArrivesAsOneRequestWithEveryReading()
    {
        var outcome = await _pipeline.ProcessAsync(
            ContractFixtures.ReadDevice("valid-buffered-backfill.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Equal(1, _pipeline.Traffic.Attempts);

        var readings = Assert.Single(_gateway.Store.Batches).Body.GetProperty("readings").EnumerateArray().ToList();
        Assert.Equal(new[] { 1450.0, 1444.2, 1438.8 }, readings.Select(r => r.GetProperty("value").GetDouble()));
        Assert.Equal(
            new[] { "2026-09-13T18:00:04Z", "2026-09-13T22:00:06Z", "2026-09-14T02:00:09Z" }.Select(DateTimeOffset.Parse),
            readings.Select(r => r.GetProperty("at").GetDateTimeOffset()));
        Assert.Equal(new[] { "ok", "ok", "uncertain" }, readings.Select(r => r.GetProperty("quality").GetString()));
    }

    /// <summary>
    /// The two contracts agree end to end: every message of levels the platform accepts is
    /// delivered over v1, and the mock — judging by the published schema, not the platform's
    /// types — accepts it. The other types need contract v2 (ADR-011).
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractFixtures.ValidDeviceForV1), MemberType = typeof(ContractFixtures))]
    public async Task EveryValidLevelMessage_IsAcceptedByTheGateway(string fileName)
    {
        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice(fileName), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Equal(new[] { HttpStatusCode.Accepted }, _pipeline.Traffic.Responses);
        Assert.Single(_gateway.Store.Batches);
    }

    /// <summary>
    /// A message at the corners of the device schema — a uint32 sequence number past 2³¹, lower-case
    /// RFC 3339 letters, a leap second — is delivered, and the gateway accepts what the platform made
    /// of the leap second. Written inline: it is not a contract example.
    /// </summary>
    [Fact]
    public async Task SchemaValidEdgeCases_ArriveAtTheGateway()
    {
        const string payload = """
            {
              "schemaVersion": 1,
              "messageId": "01J8F2K9QS4T7V0X3Z6B8N1M5G",
              "sentAt": "2017-01-01t00:00:05z",
              "sequence": 4294967295,
              "measurements": [
                { "type": "level", "value": 1449.9, "unit": "mm", "measuredAt": "2016-12-31T23:59:60Z" }
              ],
              "device": { "firmware": "1.4.2", "battery": { "value": 3.6, "unit": "V", "temperature": 21 } }
            }
            """;

        var outcome = await _pipeline.ProcessAsync(payload, AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        var reading = Assert.Single(Assert.Single(_gateway.Store.Batches).Body.GetProperty("readings").EnumerateArray());
        Assert.Equal(
            new DateTimeOffset(2016, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(TimeSpan.TicksPerSecond - 1),
            reading.GetProperty("at").GetDateTimeOffset());
    }

    /// <summary>
    /// Event Hubs delivers at least once, so the same event can be processed twice. The second
    /// delivery reaches the gateway, is accepted with the same status, and nothing is stored twice.
    /// </summary>
    [Fact]
    public async Task IdempotentReplay_IsAccepted_WithoutADuplicate()
    {
        var payload = ContractFixtures.ReadDevice("valid-full.json");

        var first = await _pipeline.ProcessAsync(payload, AuthenticatedDevice, EnqueuedAt);
        var replay = await _pipeline.ProcessAsync(payload, AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, first);
        Assert.Equal(ProcessingOutcome.Forwarded, replay);
        Assert.Equal(new[] { HttpStatusCode.Accepted, HttpStatusCode.Accepted }, _pipeline.Traffic.Responses);
        Assert.Single(_gateway.Store.Batches);
    }

    /// <summary>One token serves many messages; the identity provider is not asked per message.</summary>
    [Fact]
    public async Task Token_IsObtainedOnce_ForManyMessages()
    {
        foreach (var fileName in new[] { "valid-full.json", "valid-minimal.json", "valid-buffered-backfill.json" })
        {
            Assert.Equal(
                ProcessingOutcome.Forwarded,
                await _pipeline.ProcessAsync(ContractFixtures.ReadDevice(fileName), AuthenticatedDevice, EnqueuedAt));
        }

        Assert.Equal(1, _issuer.TokenRequests);
        Assert.Equal(3, _gateway.Store.Batches.Count);
    }

    /// <summary>
    /// A gateway that stays down exhausts the retries — the first attempt plus the standard
    /// pipeline's three retries — and the message ends as ForwardingFailed, not as an exception.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task PersistentServerError_ExhaustsTheRetries_AndYieldsForwardingFailed(HttpStatusCode status)
    {
        _pipeline.Traffic.Fault = _ => status;

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, outcome);
        Assert.Equal(4, _pipeline.Traffic.Attempts);
        Assert.Empty(_gateway.Store.Batches);
    }

    /// <summary>Transient failures — 5xx, 408, 429 — are retried, and a later attempt delivers.</summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TransientFailure_IsRetried_AndTheMessageDelivered(HttpStatusCode status)
    {
        _pipeline.Traffic.Fault = attempt => attempt == 1 ? status : null;

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Equal(new[] { status, HttpStatusCode.Accepted }, _pipeline.Traffic.Responses);
        Assert.Single(_gateway.Store.Batches);
    }

    /// <summary>Any other 4xx will fail the same way every time, so it is never retried.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ClientError_IsNotRetried(HttpStatusCode status)
    {
        _pipeline.Traffic.Fault = _ => status;

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, outcome);
        Assert.Equal(1, _pipeline.Traffic.Attempts);
    }

    /// <summary>
    /// The identity provider rotates its signing key. The platform's cached token was signed with
    /// the old key and is still within its lifetime, but the gateway — validating real RS256 JWTs —
    /// no longer trusts that key and answers 401. The platform discards the token, obtains one
    /// signed with the new key, and sends the same request once more: the message is delivered
    /// rather than lost, and the gateway stores the batch exactly once.
    /// </summary>
    [Fact]
    public async Task SigningKeyRotation_TheMessageIsDeliveredWithANewToken_AndStoredOnce()
    {
        _issuer.TokensSignedWithRetiredKey = 1;

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Equal(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Accepted }, _pipeline.Traffic.Responses);
        Assert.Equal(2, _issuer.TokenRequests);

        // The same Idempotency-Key both times, so a gateway that had stored the first attempt would
        // have deduplicated the second.
        Assert.Equal(
            new[] { "01J8F2K9QS4T7V0X3Z6B8N1M5B", "01J8F2K9QS4T7V0X3Z6B8N1M5B" },
            _pipeline.Traffic.IdempotencyKeys);

        var stored = Assert.Single(_gateway.Store.Batches);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", stored.IdempotencyKey);
        Assert.Equal(TestTokenIssuer.Subject, stored.Caller);

        // The new token is cached: the next message needs neither a retry nor a token request.
        Assert.Equal(
            ProcessingOutcome.Forwarded,
            await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt));
        Assert.Equal(2, _issuer.TokenRequests);
        Assert.Equal(3, _pipeline.Traffic.Attempts);
    }

    /// <summary>
    /// The gateway refuses even a newly obtained token — here, every token is issued for the wrong
    /// audience. That is an authentication failure, not a stale token: one retry, then
    /// ForwardingFailed, with no loop of token requests. Once the provider issues good tokens again,
    /// the next message recovers through the same single retry.
    /// </summary>
    [Fact]
    public async Task TokenRejectedTwice_FailsAfterOneRetry_AndTheNextMessageRecovers()
    {
        _issuer.IssuedAudience = "https://some-other-api.iiot.test";

        var refused = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, refused);
        Assert.Equal(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized }, _pipeline.Traffic.Responses);
        Assert.Equal(2, _issuer.TokenRequests);
        Assert.Empty(_gateway.Store.Batches);

        _issuer.IssuedAudience = TestTokenIssuer.Audience;

        var delivered = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, delivered);
        Assert.Equal(3, _issuer.TokenRequests);
        Assert.Single(_gateway.Store.Batches);
    }

    /// <summary>
    /// The two retry mechanisms compose rather than stack. A transient 503 is retried inside the
    /// resilience pipeline; the 401 that follows ends that pass, since 401 is not transient; the
    /// forwarder's own retry then delivers with a new token. One batch is stored.
    /// </summary>
    [Fact]
    public async Task TransientFailureThenRotatedKey_BothAreRetried_AndTheBatchStoredOnce()
    {
        _issuer.TokensSignedWithRetiredKey = 1;
        _pipeline.Traffic.Fault = attempt => attempt == 1 ? HttpStatusCode.ServiceUnavailable : null;

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Equal(
            new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized, HttpStatusCode.Accepted },
            _pipeline.Traffic.Responses);
        Assert.Single(_gateway.Store.Batches);
    }

    /// <summary>
    /// The bound on the worst case. The retry after a 401 is a second pass through the resilience
    /// pipeline, with its own transient retries — but only one: a gateway that then stays down costs
    /// the first attempt plus one full pipeline pass (1 + 4 attempts), never a loop.
    /// </summary>
    [Fact]
    public async Task RetryAfterUnauthorized_RunsThroughTheResiliencePipelineOnce_AndIsBounded()
    {
        _pipeline.Traffic.Fault = attempt => attempt == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable;

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, outcome);
        Assert.Equal(5, _pipeline.Traffic.Attempts);
        Assert.Equal(2, _issuer.TokenRequests);
        Assert.Empty(_gateway.Store.Batches);
    }

    /// <summary>
    /// A token endpoint that refuses the platform's credentials means nothing is sent to the
    /// gateway at all.
    /// </summary>
    [Fact]
    public async Task WrongClientSecret_FailsBeforeTheGatewayIsCalled()
    {
        await ReplacePipelineAsync(settings => settings["GatewayAuth:ClientSecret"] = "not-the-secret");

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, outcome);
        Assert.Equal(0, _pipeline.Traffic.Attempts);
    }

    /// <summary>
    /// HTTPS only: a plain-HTTP gateway address is refused before a token is even requested, so
    /// neither the token nor the readings cross the network in clear text.
    /// </summary>
    [Fact]
    public async Task PlainHttpGateway_IsRefused_BeforeAnythingIsSent()
    {
        await ReplacePipelineAsync(settings => settings["GatewayV1:BaseAddress"] = "http://gateway.iiot.test/");

        var outcome = await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, outcome);
        Assert.Equal(0, _pipeline.Traffic.Attempts);
        Assert.Equal(0, _issuer.TokenRequests);
    }

    /// <summary>
    /// Before a gateway is configured the Function must still validate and log device traffic —
    /// the end-to-end device test runs before any gateway exists. Forwarding fails with a reason;
    /// validation and the identity checks carry on as before.
    /// </summary>
    [Fact]
    public async Task NoGatewayConfigured_FailsForwarding_ButNotValidation()
    {
        await ReplacePipelineAsync(settings =>
        {
            settings.Remove("GatewayV1:BaseAddress");
            settings.Remove("GatewayAuth:ClientSecret");
        });

        Assert.Equal(
            ProcessingOutcome.ForwardingFailed,
            await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt));
        Assert.Equal(
            ProcessingOutcome.RejectedInvalidPayload,
            await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json").Replace("1.4.2", ""), AuthenticatedDevice, EnqueuedAt));
        Assert.Equal(
            ProcessingOutcome.RejectedIdentityMismatch,
            await _pipeline.ProcessAsync(ContractFixtures.ReadDevice("valid-full.json"), "tank-000999", EnqueuedAt));
    }

    /// <summary>
    /// The lifetimes the registration promises: a fresh forwarder — and so a fresh HttpClient from
    /// the factory — per resolution, one TelemetryService per invocation scope, and one token
    /// provider for the whole process so its cache is shared.
    /// </summary>
    [Fact]
    public async Task Lifetimes_NothingHoldsATypedClientBeyondOneInvocation()
    {
        var services = _pipeline.Services;

        await using (var first = services.CreateAsyncScope())
        await using (var second = services.CreateAsyncScope())
        {
            Assert.Same(
                first.ServiceProvider.GetRequiredService<TelemetryService>(),
                first.ServiceProvider.GetRequiredService<TelemetryService>());
            Assert.NotSame(
                first.ServiceProvider.GetRequiredService<TelemetryService>(),
                second.ServiceProvider.GetRequiredService<TelemetryService>());
            Assert.NotSame(
                first.ServiceProvider.GetRequiredService<IDataForwarder>(),
                first.ServiceProvider.GetRequiredService<IDataForwarder>());
            Assert.Same(
                first.ServiceProvider.GetRequiredService<IAccessTokenProvider>(),
                second.ServiceProvider.GetRequiredService<IAccessTokenProvider>());
        }

        // A scoped service resolved from the root would be a singleton in all but name. Scope
        // validation, switched on in the harness, refuses it.
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<TelemetryService>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        await _gateway.DisposeAsync();
        _issuer.Dispose();
    }

    private async Task ReplacePipelineAsync(Action<Dictionary<string, string?>> configure)
    {
        await _pipeline.DisposeAsync();
        _pipeline = new ForwardingPipeline(_gateway, _issuer, configure);
    }
}
