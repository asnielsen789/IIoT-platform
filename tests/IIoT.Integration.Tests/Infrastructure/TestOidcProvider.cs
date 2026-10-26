using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// A stand-in for the OpenID Connect provider people sign in to the dashboard with. Its discovery
/// document, signing keys and token endpoint answer the dashboard's back channel in-process; its
/// sign-in and sign-out pages are methods a test calls where a browser would follow a redirect.
/// </summary>
/// <remarks>
/// <para>
/// It checks what a real provider checks — the client's credentials, the redirect URI, and that
/// the PKCE verifier the dashboard presents hashes to the challenge it sent — so a sign-in that
/// succeeds here is evidence the dashboard ran the code flow correctly, not just that it was
/// answered.
/// </para>
/// <para>
/// The signing key and client secret exist only in memory for the life of the test run, as for
/// <see cref="TestTokenIssuer"/>. The addresses are under <c>.test</c>, a name reserved never to
/// resolve, so a request that escaped the in-process handler would fail rather than reach anyone.
/// </para>
/// </remarks>
public sealed class TestOidcProvider : IDisposable
{
    public const string Authority = "https://login.iiot.test/";
    public const string DiscoveryDocument = Authority + ".well-known/openid-configuration";
    public const string AuthorizationEndpoint = Authority + "authorize";
    public const string TokenEndpoint = Authority + "oauth/token";
    public const string JwksUri = Authority + ".well-known/jwks.json";
    public const string EndSessionEndpoint = Authority + "oidc/logout";

    public const string ClientId = "iiot-dashboard-test";
    public const string Subject = "auth0|dashboard-viewer";
    public const string DefaultUserName = "Dashboard Viewer";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private int _codesRedeemed;

    public TestOidcProvider()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>The dashboard's client secret. Generated per run.</summary>
    public string ClientSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>How many authorization codes the token endpoint has exchanged for tokens.</summary>
    public int CodesRedeemed => Volatile.Read(ref _codesRedeemed);

    /// <summary>The configuration that enables the dashboard against this provider.</summary>
    public Dictionary<string, string?> DashboardSettings() => new()
    {
        ["GatewayMock:Dashboard:Authority"] = Authority,
        ["GatewayMock:Dashboard:ClientId"] = ClientId,
        ["GatewayMock:Dashboard:ClientSecret"] = ClientSecret
    };

    /// <summary>The provider's endpoints as an in-process handler, for the dashboard's back channel.</summary>
    public HttpMessageHandler CreateBackchannelHandler() => new Backchannel(this);

    /// <summary>
    /// Plays the provider's sign-in page: checks the authorization request the dashboard sent the
    /// browser to, signs the user in, and returns what the provider posts back to the dashboard.
    /// </summary>
    /// <param name="authorizationRequest">The <c>Location</c> of the dashboard's challenge.</param>
    /// <param name="userName">The <c>name</c> claim the ID token will carry.</param>
    public AuthorizationResponse SignIn(Uri authorizationRequest, string userName = DefaultUserName)
    {
        Assert.Equal(AuthorizationEndpoint, authorizationRequest.GetLeftPart(UriPartial.Path));

        var query = HttpUtility.ParseQueryString(authorizationRequest.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("openid", (query["scope"] ?? "").Split(' '));

        var challenge = Required(query, "code_challenge");
        var redirectUri = Required(query, "redirect_uri");
        var state = Required(query, "state");
        var nonce = Required(query, "nonce");

        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new PendingCode(redirectUri, challenge, nonce, userName);

        return new AuthorizationResponse(new Uri(redirectUri), code, state);
    }

    /// <summary>
    /// Plays the provider's end-session endpoint: checks the logout request and returns where the
    /// provider sends the browser afterwards.
    /// </summary>
    public Uri EndSession(Uri logoutRequest)
    {
        Assert.Equal(EndSessionEndpoint, logoutRequest.GetLeftPart(UriPartial.Path));

        var query = HttpUtility.ParseQueryString(logoutRequest.Query);
        Assert.Equal(ClientId, query["client_id"]);

        var postLogoutRedirectUri = Required(query, "post_logout_redirect_uri");
        var state = Required(query, "state");

        return new Uri($"{postLogoutRedirectUri}?state={Uri.EscapeDataString(state)}");
    }

    public void Dispose() => _rsa.Dispose();

    private static string Required(NameValueCollection query, string name)
    {
        var value = query[name];
        Assert.False(string.IsNullOrEmpty(value), $"the request has no {name}");
        return value!;
    }

    private string CreateIdToken(PendingCode pending)
    {
        var issued = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = ClientId,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["name"] = pending.UserName,
                ["nonce"] = pending.Nonce
            },
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        });
    }

    /// <summary>What the provider posts back to the dashboard's callback (response_mode=form_post).</summary>
    public sealed record AuthorizationResponse(Uri RedirectUri, string Code, string State)
    {
        public FormUrlEncodedContent ToForm() =>
            new(new Dictionary<string, string> { ["code"] = Code, ["state"] = State });
    }

    private sealed record PendingCode(string RedirectUri, string CodeChallenge, string Nonce, string UserName);

    private sealed class Backchannel : HttpMessageHandler
    {
        private readonly TestOidcProvider _provider;

        public Backchannel(TestOidcProvider provider)
        {
            _provider = provider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!.GetLeftPart(UriPartial.Path);

            if (request.Method == HttpMethod.Get && address == DiscoveryDocument)
            {
                return Json(HttpStatusCode.OK, new Dictionary<string, object>
                {
                    ["issuer"] = Authority,
                    ["authorization_endpoint"] = AuthorizationEndpoint,
                    ["token_endpoint"] = TokenEndpoint,
                    ["jwks_uri"] = JwksUri,
                    ["end_session_endpoint"] = EndSessionEndpoint,
                    ["response_types_supported"] = new[] { "code" },
                    ["response_modes_supported"] = new[] { "query", "form_post" },
                    ["subject_types_supported"] = new[] { "public" },
                    ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                    ["code_challenge_methods_supported"] = new[] { "S256" },
                    ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post" }
                });
            }

            if (request.Method == HttpMethod.Get && address == JwksUri)
            {
                // The public half only.
                var key = _provider._rsa.ExportParameters(includePrivateParameters: false);
                return Json(HttpStatusCode.OK, new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = "RSA",
                            use = "sig",
                            alg = "RS256",
                            kid = _provider.SigningKey.KeyId,
                            n = Base64UrlEncoder.Encode(key.Modulus),
                            e = Base64UrlEncoder.Encode(key.Exponent)
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Post && address == TokenEndpoint)
            {
                return Redeem(HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken)));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage Redeem(NameValueCollection form)
        {
            if (form["grant_type"] != "authorization_code")
            {
                return Error(HttpStatusCode.BadRequest, "unsupported_grant_type");
            }

            if (form["client_id"] != ClientId || form["client_secret"] != _provider.ClientSecret)
            {
                return Error(HttpStatusCode.Unauthorized, "invalid_client");
            }

            // A code is good for one redemption.
            if (form["code"] is not { } code || !_provider._codes.TryRemove(code, out var pending))
            {
                return Error(HttpStatusCode.BadRequest, "invalid_grant");
            }

            if (form["redirect_uri"] != pending.RedirectUri)
            {
                return Error(HttpStatusCode.BadRequest, "invalid_grant");
            }

            // PKCE, S256: the verifier must hash to the challenge the authorization request carried.
            var verifier = form["code_verifier"];
            if (string.IsNullOrEmpty(verifier) ||
                Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != pending.CodeChallenge)
            {
                return Error(HttpStatusCode.BadRequest, "invalid_grant");
            }

            Interlocked.Increment(ref _provider._codesRedeemed);

            return Json(HttpStatusCode.OK, new
            {
                id_token = _provider.CreateIdToken(pending),
                access_token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
                token_type = "Bearer",
                expires_in = 3600
            });
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string error) =>
            Json(status, new { error });

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
