using System.Net;
using System.Text.Json;
using IIoT.Core.Authentication;
using IIoT.Core.Forwarders;
using IIoT.Core.Models;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Tests.Http;
using IIoT.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// The forwarder's own HTTP behaviour, against a stub handler. Retries are not tested here: they
/// belong to the resilience handler the client is registered with, and are covered end to end in
/// IIoT.Integration.Tests.
/// </summary>
public class GatewayV1ForwarderTests
{
    private const string AccessToken = "access-token-for-tests";
    private static readonly ForwardingContext Context = new("tank-000123", new DateTimeOffset(2026, 9, 14, 6, 0, 13, TimeSpan.Zero));

    private readonly RecordingTokenProvider _tokens = new(AccessToken);

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
    /// A 401 means the gateway no longer accepts a token the cache still thinks is valid. It is
    /// not retried, but the token is discarded so the next message gets a new one.
    /// </summary>
    [Fact]
    public async Task Forward_Unauthorized_InvalidatesTheToken()
    {
        var handler = Respond(HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-minimal.json"), Context));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(new[] { AccessToken }, _tokens.Invalidated);
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

    private GatewayV1Forwarder Create(StubHttpMessageHandler handler, string? baseAddress) =>
        new(
            new HttpClient(handler),
            _tokens,
            Options.Create(new GatewayV1Options { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) }),
            NullLogger<GatewayV1Forwarder>.Instance);

    private static StubHttpMessageHandler Respond(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status));

    private static TelemetryMessage Parse(string fileName) =>
        TelemetryValidator.Validate(ContractFixtures.Read(fileName)).Message!;
}
