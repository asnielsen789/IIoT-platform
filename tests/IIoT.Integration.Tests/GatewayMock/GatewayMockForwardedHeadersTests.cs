using System.Net;
using System.Web;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// Behind a TLS-terminating proxy the app sees plain HTTP. The dashboard's sign-in must still ask
/// the provider to come back to <c>https://</c>, from the proxy's <c>X-Forwarded-Proto</c> — and
/// must believe that header only from the proxy.
/// </summary>
/// <remarks>
/// Each request is built on the test server directly, because only there can a test set the
/// address a request comes from: the proxy's, or anyone else's.
/// </remarks>
public class GatewayMockForwardedHeadersTests : IClassFixture<DashboardFixture>
{
    /// <summary>The host the mock is reached at, as the proxy passes it on.</summary>
    private const string PublicHost = "gateway-mock.iiot.test";

    /// <summary>Inside <see cref="DashboardFixture.TrustedProxyNetwork"/>.</summary>
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.20.30.40");

    /// <summary>TEST-NET-3 (RFC 5737): an address on the internet, not the proxy's.</summary>
    private static readonly IPAddress Stranger = IPAddress.Parse("203.0.113.7");

    private readonly DashboardFixture _dashboard;

    public GatewayMockForwardedHeadersTests(DashboardFixture dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>
    /// The hosted case: a browser on HTTPS, a proxy that terminates TLS, and the app on HTTP. The
    /// challenge is an authorization code request with PKCE, and its redirect URI is HTTPS.
    /// </summary>
    [Fact]
    public async Task Challenge_BehindTheTrustedProxy_RedirectsBackToHttps()
    {
        var authorization = await ChallengeAsync(TrustedProxy, ("X-Forwarded-Proto", "https"));

        Assert.Equal(TestOidcProvider.AuthorizationEndpoint, authorization.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(authorization.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["code_challenge"]);
        Assert.Equal($"https://{PublicHost}{DashboardFixture.CallbackPath}", query["redirect_uri"]);
    }

    /// <summary>
    /// The same header from anywhere else is ignored. A provider refuses the resulting HTTP
    /// redirect URI, which is the right outcome for a request that did not come through the proxy.
    /// </summary>
    [Fact]
    public async Task Challenge_FromAnUntrustedAddress_IgnoresTheForwardedScheme()
    {
        var authorization = await ChallengeAsync(Stranger, ("X-Forwarded-Proto", "https"));

        Assert.Equal(
            $"http://{PublicHost}{DashboardFixture.CallbackPath}",
            HttpUtility.ParseQueryString(authorization.Query)["redirect_uri"]);
    }

    /// <summary>
    /// Only the value the proxy next to the app wrote, the rightmost, counts. One a client put in
    /// front of it does not.
    /// </summary>
    [Fact]
    public async Task Challenge_UsesOnlyTheRightmostForwardedValue()
    {
        var authorization = await ChallengeAsync(TrustedProxy, ("X-Forwarded-Proto", "https, http"));

        Assert.Equal(
            $"http://{PublicHost}{DashboardFixture.CallbackPath}",
            HttpUtility.ParseQueryString(authorization.Query)["redirect_uri"]);
    }

    /// <summary>
    /// <c>X-Forwarded-Host</c> is not believed even from the proxy: it would choose the host the
    /// provider sends the authorization code to.
    /// </summary>
    [Fact]
    public async Task Challenge_IgnoresAForwardedHost()
    {
        var authorization = await ChallengeAsync(
            TrustedProxy, ("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "attacker.iiot.test"));

        Assert.Equal(
            $"https://{PublicHost}{DashboardFixture.CallbackPath}",
            HttpUtility.ParseQueryString(authorization.Query)["redirect_uri"]);
    }

    /// <summary>Requests <c>GET /dashboard</c> without a session, as arriving from <paramref name="from"/>, and returns where the challenge redirects.</summary>
    private async Task<Uri> ChallengeAsync(IPAddress from, params (string Name, string Value)[] headers)
    {
        var context = await _dashboard.Factory.Server.SendAsync(request =>
        {
            request.Request.Method = HttpMethods.Get;
            request.Request.Scheme = "http";
            request.Request.Host = new HostString(PublicHost);
            request.Request.Path = DashboardFixture.DashboardPath;
            foreach (var (name, value) in headers)
            {
                request.Request.Headers.Append(name, value);
            }

            request.Connection.RemoteIpAddress = from;
        });

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        return new Uri(context.Response.Headers.Location.ToString());
    }
}
