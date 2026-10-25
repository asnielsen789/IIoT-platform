using System.Net;
using System.Text.Json;
using IIoT.Core.Authentication;
using IIoT.Core.Forwarders;
using IIoT.Core.Models;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Tests.Http;
using IIoT.Core.Tests.Logging;
using IIoT.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// The forwarder's own HTTP behaviour, against a stub handler. Transient retries are not tested
/// here: they belong to the resilience handler the client is registered with, and are covered end
/// to end in IIoT.Integration.Tests. The one retry the forwarder makes itself, on a 401, is.
/// </summary>
public class GatewayV1ForwarderTests
{
    private const string AccessToken = "access-token-for-tests";

    /// <summary>What the provider hands out once <see cref="AccessToken"/> has been invalidated.</summary>
    private const string NewAccessToken = "new-access-token-for-tests";

    private static readonly ForwardingContext Context = new("tank-000123", new DateTimeOffset(2026, 9, 14, 6, 0, 13, TimeSpan.Zero));

    private readonly RecordingTokenProvider _tokens = new(AccessToken, NewAccessToken);

    [Fact]
    public async Task Forward_PostsTheBatchWithBearerTokenAndIdempotencyKey()
    {
        var handler = Respond(HttpStatusCode.Accepted);
        var forwarder = Create(handler, "https://gateway.iiot.test/");

        await forwarder.ForwardAsync(Parse("valid-full.json"), Context);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://gateway.iiot.test/v1/readings"), request.Uri);
        Assert.Equal("Bearer", request.Authorization!.Scheme);
        Assert.Equal(AccessToken, request.Authorization.Parameter);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", request.Headers["Idempotency-Key"]);
        Assert.Equal("application/json", request.ContentType);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("tank-000123", body.RootElement.GetProperty("deviceId").GetString());
        Assert.Empty(GatewaySchema.Errors(System.Text.Encoding.UTF8.GetBytes(request.Body!)));
    }

    /// <summary>
    /// Buffered backfill is one request with every reading — never one call per reading (ADR-004).
    /// </summary>
    [Fact]
    public async Task Forward_BufferedBackfill_IsOneRequest()
    {
        var handler = Respond(HttpStatusCode.Accepted);

        await Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-buffered-backfill.json"), Context);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(3, body.RootElement.GetProperty("readings").GetArrayLength());
    }

    [Theory]
    [InlineData("https://gateway.iiot.test", "https://gateway.iiot.test/v1/readings")]
    [InlineData("https://gateway.iiot.test/", "https://gateway.iiot.test/v1/readings")]
    [InlineData("https://gateway.iiot.test/api", "https://gateway.iiot.test/api/v1/readings")]
    [InlineData("https://gateway.iiot.test/api/", "https://gateway.iiot.test/api/v1/readings")]
    [InlineData("https://gateway.iiot.test:8443/a/b", "https://gateway.iiot.test:8443/a/b/v1/readings")]
    public async Task Forward_AppendsTheReadingsPathToTheBaseAddress(string baseAddress, string expected)
    {
        var handler = Respond(HttpStatusCode.Accepted);

        await Create(handler, baseAddress).ForwardAsync(Parse("valid-minimal.json"), Context);

        Assert.Equal(new Uri(expected), Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Forward_AnySuccessStatus_Completes(HttpStatusCode status)
    {
        var handler = Respond(status);

        await Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context);

        Assert.Single(handler.Requests);
        Assert.Empty(_tokens.Invalidated);
    }

    /// <summary>
    /// The response this class sees is already final — retries happened in the handler pipeline —
    /// so a failure becomes an exception that names the status and the gateway's own reason.
    /// </summary>
    [Fact]
    public async Task Forward_Refused_ThrowsWithStatusAndTheGatewaysReason()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{ "title": "Request rejected", "status": 400, "detail": "/readings/0/quality: Required properties [\"quality\"] are not present" }"""));

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.StartsWith("Gateway v1 responded 400 BadRequest: /readings/0/quality", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Forward_FailureWithoutAProblemBody_ThrowsWithTheStatus(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("<html>oops</html>") });

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal($"Gateway v1 responded {(int)status} {status}", exception.Message);
        Assert.Empty(_tokens.Invalidated);
    }

    /// <summary>
    /// A 401 means the gateway no longer accepts a token the cache still thinks is valid — the
    /// identity provider has rotated its signing keys, say. The token is discarded and the same
    /// request sent once more with a new one: same address, same body, same Idempotency-Key, so a
    /// gateway that did store the first attempt deduplicates the second.
    /// </summary>
    [Fact]
    public async Task Forward_Unauthorized_RetriesOnceWithANewToken_AndTheSameRequest()
    {
        var handler = new StubHttpMessageHandler(request => new HttpResponseMessage(
            request.Authorization?.Parameter == AccessToken ? HttpStatusCode.Unauthorized : HttpStatusCode.Accepted));

        await Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-full.json"), Context);

        Assert.Equal(new[] { AccessToken, NewAccessToken }, handler.Requests.Select(request => request.Authorization!.Parameter));
        Assert.Equal(new[] { AccessToken }, _tokens.Invalidated);

        var (first, retry) = (handler.Requests[0], handler.Requests[1]);
        Assert.Equal(first.Uri, retry.Uri);
        Assert.Equal(first.Body, retry.Body);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", retry.Headers["Idempotency-Key"]);
        Assert.Equal(first.Headers["Idempotency-Key"], retry.Headers["Idempotency-Key"]);
        Assert.Equal("application/json", retry.ContentType);
    }

    /// <summary>
    /// A token issued moments ago refused as well means the token is not the problem: the gateway
    /// does not trust what the provider issues. That is an authentication failure, reported as
    /// one, and the forwarder stops after the one retry instead of asking for token after token.
    /// </summary>
    [Fact]
    public async Task Forward_UnauthorizedTwice_IsAnAuthenticationFailure_AndStops()
    {
        var handler = Respond(HttpStatusCode.Unauthorized);
        var logger = new CapturingLogger<GatewayV1Forwarder>();

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/", logger).ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.StartsWith("Gateway v1 authentication failed: it responded 401 Unauthorized to a newly obtained token", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, _tokens.Requests);

        // The new token is kept: another would fare no better, and the next message trying it
        // first costs a gateway request rather than a call to the rate-limited token endpoint.
        Assert.Equal(new[] { AccessToken }, _tokens.Invalidated);

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.StartsWith("Authentication failure: gateway v1 rejected a newly obtained access token", error.Message, StringComparison.Ordinal);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5A", error.Properties["MessageId"]);
        Assert.Equal("tank-000123", error.Properties["DeviceId"]);

        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(AccessToken, entry.Everything, StringComparison.Ordinal);
            Assert.DoesNotContain(NewAccessToken, entry.Everything, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// After the retry, any other answer is handled as it always is — here a refusal, reported with
    /// the gateway's reason. The retry's answer is the one that counts.
    /// </summary>
    [Fact]
    public async Task Forward_UnauthorizedThenRefused_ReportsTheRetrysAnswer()
    {
        var handler = new StubHttpMessageHandler(request => request.Authorization?.Parameter == AccessToken
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{ "status": 400, "detail": "/readings/0/quality: missing" }"""));

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("Gateway v1 responded 400 BadRequest: /readings/0/quality: missing", exception.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>When no new token can be had, nothing is sent a second time.</summary>
    [Fact]
    public async Task Forward_UnauthorizedAndNoNewToken_FailsWithTheTokenProvidersReason()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            _tokens.FailWith = new AccessTokenException("Token endpoint responded 503 ServiceUnavailable");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        await Assert.ThrowsAsync<AccessTokenException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Single(handler.Requests);
        Assert.Equal(new[] { AccessToken }, _tokens.Invalidated);
    }

    /// <summary>
    /// A 403 is not retried: the gateway accepted the token and refused the caller, and a new token
    /// for the same client changes nothing. The token stays cached, since it was not rejected.
    /// </summary>
    [Fact]
    public async Task Forward_Forbidden_IsNotRetried_AndKeepsTheToken()
    {
        var handler = Respond(HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal(1, _tokens.Requests);
        Assert.Empty(_tokens.Invalidated);
    }

    public static TheoryData<string?, string> RefusedBaseAddresses => new()
    {
        { null, "Gateway v1 BaseAddress is not configured" },
        { "http://gateway.iiot.test/", "Gateway v1 BaseAddress must use https" },
        { "http://localhost.gateway.iiot.test/", "Gateway v1 BaseAddress must use https" }
    };

    /// <summary>
    /// HTTPS only. A plain-HTTP gateway would receive the bearer token and the readings in clear
    /// text, so nothing is sent at all — not even a request for the token.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedBaseAddresses))]
    public async Task Forward_InsecureOrMissingBaseAddress_IsRefusedBeforeAnythingIsSent(string? baseAddress, string expected)
    {
        var handler = Respond(HttpStatusCode.Accepted);

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, baseAddress).ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.StartsWith(expected, exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, _tokens.Requests);
    }

    /// <summary>The loopback exception the in-process tests rely on.</summary>
    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1:5000/")]
    public async Task Forward_PlainHttpToLoopback_IsAllowed(string baseAddress)
    {
        var handler = Respond(HttpStatusCode.Accepted);

        await Create(handler, baseAddress).ForwardAsync(Parse("valid-minimal.json"), Context);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Forward_NoToken_SendsNothing()
    {
        var handler = Respond(HttpStatusCode.Accepted);
        _tokens.FailWith = new AccessTokenException("Token endpoint responded 401 Unauthorized: invalid_client");

        await Assert.ThrowsAsync<AccessTokenException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forward_UnmappableMessage_SendsNothing()
    {
        var handler = Respond(HttpStatusCode.Accepted);
        var message = Parse("valid-minimal.json") with
        {
            Measurements = [new Measurement { Type = "temperature", Value = 4, Unit = "Cel", MeasuredAt = Context.EnqueuedAt }]
        };

        await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(message, Context));

        Assert.Empty(handler.Requests);
        Assert.Equal(0, _tokens.Requests);
    }

    private GatewayV1Forwarder Create(StubHttpMessageHandler handler, string? baseAddress, ILogger<GatewayV1Forwarder>? logger = null) =>
        new(
            new HttpClient(handler),
            _tokens,
            Options.Create(new GatewayV1Options { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) }),
            logger ?? NullLogger<GatewayV1Forwarder>.Instance);

    private static StubHttpMessageHandler Respond(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status));

    private static TelemetryMessage Parse(string fileName) =>
        TelemetryValidator.Validate(ContractFixtures.Read(fileName)).Message!;
}
