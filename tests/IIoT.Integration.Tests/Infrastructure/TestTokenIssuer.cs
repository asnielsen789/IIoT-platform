using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// A stand-in for the identity provider: a token endpoint that implements the client credentials
/// grant, signing JWTs with an RSA key generated when the issuer is created and handing the mock
/// only the public half.
/// </summary>
/// <remarks>
/// The key and the client secret exist only in memory for the life of the test run. Nothing is
/// read from or written to a file, so there is no test credential in the repository to be
/// mistaken for a real one, or to leak.
/// </remarks>
public sealed class TestTokenIssuer : IDisposable
{
    public const string Issuer = "https://token-issuer.iiot.test/";
    public const string TokenEndpoint = "https://token-issuer.iiot.test/oauth/token";
    public const string Audience = "https://gateway.iiot.test";
    public const string ClientId = "iiot-platform-test";

    /// <summary>The subject an M2M token carries: <c>{client id}@clients</c>, as Auth0 issues it.</summary>
    public const string Subject = ClientId + "@clients";

    private readonly RSA _rsa = RSA.Create(2048);
    private int _tokenRequests;

    public TestTokenIssuer()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>The client secret the token endpoint expects. Generated per run.</summary>
    public string ClientSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The audience the endpoint puts into the tokens it issues. Changing it simulates a provider
    /// that issues tokens the gateway will not accept.
    /// </summary>
    public string IssuedAudience { get; set; } = Audience;

    /// <summary>How many requests reached the token endpoint.</summary>
    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    /// <summary>
    /// The token endpoint as an in-process handler, for the platform's token client. It checks
    /// what a real provider checks — grant type, client credentials, audience — so the platform's
    /// request is verified, not just answered.
    /// </summary>
    public HttpMessageHandler CreateTokenEndpointHandler() => new TokenEndpointHandler(this);

    /// <summary>What the gateway is configured to trust. A public key, so not a secret.</summary>
    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    /// <summary>The configuration the mock needs to trust this issuer.</summary>
    public Dictionary<string, string?> GatewaySettings() => new()
    {
        ["GatewayMock:Jwt:Authority"] = "",
        ["GatewayMock:Jwt:Issuer"] = Issuer,
        ["GatewayMock:Jwt:Audience"] = Audience,
        ["GatewayMock:Jwt:SigningKeys:0"] = PublicKeyPem
    };

    /// <summary>
    /// A token that the mock accepts unless one of the arguments says otherwise.
    /// </summary>
    public string CreateToken(
        string audience = Audience,
        string issuer = Issuer,
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        SigningCredentials? signingCredentials = null,
        bool unsigned = false)
    {
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = Subject },
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued + (lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = unsigned
                ? null
                : signingCredentials ?? new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public void Dispose() => _rsa.Dispose();

    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        private readonly TestTokenIssuer _issuer;

        public TokenEndpointHandler(TestTokenIssuer issuer)
        {
            _issuer = issuer;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _issuer._tokenRequests);

            if (request.Method != HttpMethod.Post || request.RequestUri != new Uri(TokenEndpoint))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));

            if (form["grant_type"] != "client_credentials")
            {
                return Error(HttpStatusCode.BadRequest, "unsupported_grant_type");
            }

            if (form["client_id"] != ClientId || form["client_secret"] != _issuer.ClientSecret)
            {
                return Error(HttpStatusCode.Unauthorized, "access_denied");
            }

            if (form["audience"] != Audience)
            {
                return Error(HttpStatusCode.Forbidden, "access_denied");
            }

            var token = _issuer.CreateToken(audience: _issuer.IssuedAudience);
            return Json(HttpStatusCode.OK, new { access_token = token, token_type = "Bearer", expires_in = 3600 });
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string error) =>
            Json(status, new { error });

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
