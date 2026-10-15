using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The mock validates the token on every request — signature, issuer, audience and lifetime —
/// and nothing reaches the store without passing all four.
/// </summary>
public class GatewayMockAuthenticationTests : IClassFixture<GatewayMockFixture>
{
    private const string DeviceId = "tank-000123";

    private readonly GatewayMockFixture _gateway;

    public GatewayMockAuthenticationTests(GatewayMockFixture gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// The positive control. Without it, every rejection below could be the mock rejecting
    /// everything, and the suite would still pass.
    /// </summary>
    [Fact]
    public async Task Post_ValidToken_IsAccepted()
    {
        var messageId = GatewayRequests.NewMessageId();

        using var response = await PostAsync(_gateway.Issuer.CreateToken(), messageId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
        Assert.Equal(TestTokenIssuer.Subject, stored.Caller);
    }

    [Fact]
    public async Task Post_NoToken_IsUnauthorized()
    {
        var messageId = GatewayRequests.NewMessageId();

        using var response = await PostAsync(bearerToken: null, messageId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Bearer");
        AssertNothingStored(messageId);
    }

    public static TheoryData<string> RejectedTokens => new()
    {
        "expired",
        "not yet valid",
        "wrong audience",
        "wrong issuer",
        "signed by an untrusted key",
        "unsigned (alg none)",
        "HS256 signed with the public key",
        "not a JWT"
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Post_InvalidToken_IsUnauthorized(string kind)
    {
        var messageId = GatewayRequests.NewMessageId();

        using var response = await PostAsync(CreateRejectedToken(kind), messageId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingStored(messageId);
    }

    /// <summary>A valid token presented under any scheme but Bearer is not a bearer token.</summary>
    [Fact]
    public async Task Post_TokenUnderAnotherScheme_IsUnauthorized()
    {
        var messageId = GatewayRequests.NewMessageId();
        using var request = GatewayRequests.PostReadings(
            GatewayRequests.ValidBatch(DeviceId, messageId), bearerToken: null, messageId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _gateway.Issuer.CreateToken());

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingStored(messageId);
    }

    /// <summary>
    /// Authentication runs before anything looks at the body, so an anonymous caller learns
    /// nothing about the contract from a 400 — it only ever sees 401.
    /// </summary>
    [Fact]
    public async Task Post_NoTokenAndInvalidBody_IsUnauthorizedNotBadRequest()
    {
        using var request = GatewayRequests.PostReadings("{ not json", bearerToken: null, idempotencyKey: null);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The mock has no anonymous endpoints, including ones it does not define.</summary>
    [Theory]
    [InlineData("GET", "/v1/readings")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/health")]
    public async Task OtherRoutes_WithoutToken_DoNotSucceed(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode, $"{method} {path} answered {(int)response.StatusCode}");
    }

    private string CreateRejectedToken(string kind)
    {
        var issuer = _gateway.Issuer;

        switch (kind)
        {
            case "expired":
                // Well outside the mock's one-minute clock skew.
                return issuer.CreateToken(issuedAt: DateTimeOffset.UtcNow.AddHours(-2), lifetime: TimeSpan.FromHours(1));

            case "not yet valid":
                return issuer.CreateToken(issuedAt: DateTimeOffset.UtcNow.AddHours(1));

            case "wrong audience":
                // Same issuer, same key — a token minted for another API of the same tenant.
                return issuer.CreateToken(audience: "https://some-other-api.iiot.test");

            case "wrong issuer":
                return issuer.CreateToken(issuer: "https://impostor.iiot.test/");

            case "signed by an untrusted key":
                using (var untrusted = RSA.Create(2048))
                {
                    return issuer.CreateToken(signingCredentials: new SigningCredentials(
                        new RsaSecurityKey(untrusted.ExportParameters(true)), SecurityAlgorithms.RsaSha256));
                }

            case "unsigned (alg none)":
                return issuer.CreateToken(unsigned: true);

            case "HS256 signed with the public key":
                // The classic algorithm-confusion attack: the public key is public, so if the
                // gateway accepted HS256 with it as the shared secret, anyone could mint tokens.
                return issuer.CreateToken(signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(issuer.PublicKeyPem)), SecurityAlgorithms.HmacSha256));

            case "not a JWT":
                return "this-is-not-a-jwt";

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown token kind");
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string? bearerToken, string messageId)
    {
        using var request = GatewayRequests.PostReadings(
            GatewayRequests.ValidBatch(DeviceId, messageId), bearerToken, messageId);

        return await _gateway.Client.SendAsync(request);
    }

    private void AssertNothingStored(string messageId) =>
        Assert.DoesNotContain(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
}
