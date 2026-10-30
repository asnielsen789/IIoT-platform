using System.Net;
using IIoT.Core.Forwarders;
using IIoT.Core.Models;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Tests.Http;
using IIoT.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// What is v2's own in <see cref="GatewayV2Forwarder"/>: the path, the body and the setting's name.
/// The request, the token and the failure handling are <see cref="GatewayForwarder"/>'s, shared with
/// v1 and covered in <see cref="GatewayV1ForwarderTests"/>; the 401 retry is checked here once, to
/// show v2 inherits it.
/// </summary>
public class GatewayV2ForwarderTests
{
    private const string AccessToken = "access-token-for-tests";
    private const string NewAccessToken = "new-access-token-for-tests";

    private static readonly ForwardingContext Context = new("wash-000123", new DateTimeOffset(2026, 9, 14, 6, 0, 13, TimeSpan.Zero));

    private readonly RecordingTokenProvider _tokens = new(AccessToken, NewAccessToken);

    [Fact]
    public async Task Forward_PostsAV2BodyToTheV2Path()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        await Create(handler, "https://gateway.iiot.test/api").ForwardAsync(Parse("valid-car-wash.json"), Context);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://gateway.iiot.test/api/v2/readings"), request.Uri);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M6B", request.Headers["Idempotency-Key"]);
        Assert.Empty(GatewayV2Schema.Errors(System.Text.Encoding.UTF8.GetBytes(request.Body!)));
    }

    [Fact]
    public async Task Forward_Unauthorized_RetriesOnceWithANewToken()
    {
        var handler = new StubHttpMessageHandler(request => new HttpResponseMessage(
            request.Authorization!.Parameter == AccessToken ? HttpStatusCode.Unauthorized : HttpStatusCode.Accepted));

        await Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-car-wash.json"), Context);

        Assert.Equal([AccessToken, NewAccessToken], handler.Requests.Select(request => request.Authorization!.Parameter));
    }

    [Fact]
    public async Task Forward_WithoutABaseAddress_NamesV2sSetting()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, null).ForwardAsync(Parse("valid-car-wash.json"), Context));

        Assert.StartsWith("Gateway v2 BaseAddress is not configured", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Forward_Refused_ReportsV2()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<ForwardingException>(() =>
            Create(handler, "https://gateway.iiot.test/").ForwardAsync(Parse("valid-car-wash.json"), Context));

        Assert.StartsWith("Gateway v2 responded 400", exception.Message, StringComparison.Ordinal);
    }

    private GatewayV2Forwarder Create(StubHttpMessageHandler handler, string? baseAddress) =>
        new(
            new HttpClient(handler),
            _tokens,
            Options.Create(new GatewayV2Options { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) }),
            NullLogger<GatewayV2Forwarder>.Instance);

    private static TelemetryMessage Parse(string fileName) =>
        TelemetryValidator.Validate(ContractFixtures.Read(fileName)).Message!;
}
