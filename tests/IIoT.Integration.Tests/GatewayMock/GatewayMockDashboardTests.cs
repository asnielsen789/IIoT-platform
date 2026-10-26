using System.Net;
using System.Web;
using IIoT.Integration.Tests.Infrastructure;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The dashboard, enabled: a person signs in through the OpenID Connect provider and sees what the
/// mock received. Its session and the API's bearer token never stand in for each other.
/// </summary>
public class GatewayMockDashboardTests : IClassFixture<DashboardFixture>
{
    private readonly DashboardFixture _dashboard;

    public GatewayMockDashboardTests(DashboardFixture dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>
    /// A browser without a session is sent to the provider with an authorization code request
    /// that carries a PKCE challenge, not served the page and not given a token in the redirect.
    /// </summary>
    [Fact]
    public async Task Dashboard_WithoutASession_RedirectsToTheProviderWithPkce()
    {
        using var browser = _dashboard.CreateBrowser();

        using var response = await browser.GetAsync(DashboardFixture.DashboardPath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(TestOidcProvider.AuthorizationEndpoint, location.GetLeftPart(UriPartial.Path));

        var query = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("form_post", query["response_mode"]);
        Assert.Equal("S256", query["code_challenge_method"]);

        // 32 random bytes, hashed with SHA-256 and base64url-encoded without padding.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["code_challenge"]);
        Assert.Equal("https://localhost" + DashboardFixture.CallbackPath, query["redirect_uri"]);
    }

    /// <summary>
    /// The API's bearer token does not open the dashboard: the dashboard asks only its own scheme,
    /// so a valid token is ignored and the browser is sent to sign in.
    /// </summary>
    [Fact]
    public async Task Dashboard_WithOnlyABearerToken_IsNotServed()
    {
        using var browser = _dashboard.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, DashboardFixture.DashboardPath);
        request.Headers.Authorization = new("Bearer", _dashboard.Issuer.CreateToken());

        using var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(TestOidcProvider.AuthorizationEndpoint, response.Headers.Location!.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Dashboard_SignedIn_ShowsTheStoredBatches()
    {
        var messageId = GatewayRequests.NewMessageId();
        const string DeviceId = "tank-dashboard-1";
        await DeliverAsync(GatewayRequests.ValidBatch(DeviceId, messageId, "2026-09-14T06:00:13Z"), messageId);

        var redeemedBefore = _dashboard.Provider.CodesRedeemed;
        var (browser, _) = await _dashboard.SignInAsync();
        using (browser)
        {
            using var response = await browser.GetAsync(DashboardFixture.DashboardPath);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(redeemedBefore + 1, _dashboard.Provider.CodesRedeemed);

            Assert.Contains(DeviceId, html, StringComparison.Ordinal);
            Assert.Contains(messageId, html, StringComparison.Ordinal);
            Assert.Contains("2026-09-14T06:00:13Z", html, StringComparison.Ordinal);
            Assert.Contains(TestTokenIssuer.Subject, html, StringComparison.Ordinal);
            Assert.Contains("1432.5", html, StringComparison.Ordinal);
            Assert.Contains(TestOidcProvider.DefaultUserName, html, StringComparison.Ordinal);

            Assert.Contains("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Everything on the page came from a request body or a token. A device ID, message ID,
    /// firmware version or user name that is markup is shown as text, and no script reaches the
    /// browser — and the response forbids script in any case.
    /// </summary>
    [Fact]
    public async Task Dashboard_RendersUntrustedValuesAsText()
    {
        const string DeviceId = "<script>alert(1)</script>";
        const string MessageId = "<script>alert(2)</script>";
        const string Firmware = "<script>alert(3)</script>";
        const string UserName = "<script>alert(4)</script>";

        var body = System.Text.Json.Nodes.JsonNode.Parse(GatewayRequests.ValidBatch(DeviceId, MessageId))!;
        body["firmware"] = Firmware;
        await DeliverAsync(body.ToJsonString(), MessageId);

        var (browser, _) = await _dashboard.SignInAsync(UserName);
        using (browser)
        {
            using var response = await browser.GetAsync(DashboardFixture.DashboardPath);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            for (var i = 1; i <= 4; i++)
            {
                Assert.Contains($"&lt;script&gt;alert({i})&lt;/script&gt;", html, StringComparison.Ordinal);
            }

            var policy = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("script-src", policy, StringComparison.Ordinal);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.True(response.Headers.CacheControl?.NoStore, "the page must not be cached");
        }
    }

    /// <summary>
    /// The session cookie never authenticates an API call — not when a browser attaches it, which
    /// its path already prevents, and not when anything else sends it either. The positive control
    /// first: the same cookie does open the dashboard.
    /// </summary>
    [Fact]
    public async Task Readings_WithOnlyTheDashboardCookie_IsUnauthorized()
    {
        var (browser, sessionCookie) = await _dashboard.SignInAsync();
        using (browser)
        {
            using var page = await browser.GetAsync(DashboardFixture.DashboardPath);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }

        using var api = _dashboard.Factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        var messageId = GatewayRequests.NewMessageId();
        using var request = GatewayRequests.PostReadings(
            GatewayRequests.ValidBatch("tank-dashboard-2", messageId), bearerToken: null, messageId);
        request.Headers.Add("Cookie", sessionCookie);

        using var response = await api.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Bearer");
        Assert.DoesNotContain(_dashboard.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    [Fact]
    public async Task SignIn_IssuesAHardenedSessionCookie()
    {
        using var browser = _dashboard.CreateBrowser();
        using var challenge = await browser.GetAsync(DashboardFixture.DashboardPath);
        var signIn = _dashboard.Provider.SignIn(challenge.Headers.Location!);
        using var form = signIn.ToForm();

        using var callback = await browser.PostAsync(signIn.RedirectUri, form);

        var cookie = Assert.Single(
            callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(DashboardFixture.SessionCookieName + "=", StringComparison.Ordinal));
        var attributes = cookie.Split(';').Skip(1).Select(attribute => attribute.Trim().ToLowerInvariant()).ToList();

        Assert.Contains("path=" + DashboardFixture.DashboardPath, attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=lax", attributes);

        // A browser-session cookie: no expiry of its own, so it does not outlive the browser.
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("expires=", StringComparison.Ordinal));
    }

    /// <summary>
    /// An authorization code is useless without the PKCE verifier of the sign-in it was issued
    /// for. A code obtained for one browser's sign-in, injected into another's, is refused at the
    /// provider, and no session is issued. Also guards the test provider: one that accepted any
    /// verifier would make every sign-in above meaningless.
    /// </summary>
    [Fact]
    public async Task SignIn_WithACodeIssuedForAnotherSignIn_IssuesNoSession()
    {
        using var victim = _dashboard.CreateBrowser();
        using var attacker = _dashboard.CreateBrowser();
        using var victimChallenge = await victim.GetAsync(DashboardFixture.DashboardPath);
        using var attackerChallenge = await attacker.GetAsync(DashboardFixture.DashboardPath);

        var stolen = _dashboard.Provider.SignIn(victimChallenge.Headers.Location!);
        var attackersOwn = HttpUtility.ParseQueryString(attackerChallenge.Headers.Location!.Query)["state"]!;
        var redeemedBefore = _dashboard.Provider.CodesRedeemed;

        using var form = (stolen with { State = attackersOwn }).ToForm();
        using var callback = await attacker.PostAsync(stolen.RedirectUri, form);

        Assert.NotEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.False(
            callback.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie => cookie.StartsWith(DashboardFixture.SessionCookieName + "=", StringComparison.Ordinal)),
            "a sign-in with another sign-in's code must not issue a session");
        Assert.Equal(redeemedBefore, _dashboard.Provider.CodesRedeemed);
    }

    /// <summary>
    /// Sign-out ends the session here and at the provider, with the client identified and an
    /// HTTPS address to come back to; afterwards the dashboard asks for a new sign-in.
    /// </summary>
    [Fact]
    public async Task SignOut_EndsTheSessionHereAndAtTheProvider()
    {
        var (browser, _) = await _dashboard.SignInAsync();
        using (browser)
        {
            using var signOut = await browser.GetAsync(DashboardFixture.SignOutPath);

            Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
            var deleted = Assert.Single(
                signOut.Headers.GetValues("Set-Cookie"),
                cookie => cookie.StartsWith(DashboardFixture.SessionCookieName + "=", StringComparison.Ordinal));
            Assert.Contains("expires=Thu, 01 Jan 1970", deleted, StringComparison.Ordinal);

            var logout = signOut.Headers.Location!;
            Assert.Equal(
                "https://localhost" + DashboardFixture.SignedOutCallbackPath,
                HttpUtility.ParseQueryString(logout.Query)["post_logout_redirect_uri"]);

            using var back = await browser.GetAsync(_dashboard.Provider.EndSession(logout));
            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
            Assert.Equal(DashboardFixture.DashboardPath, back.Headers.Location?.OriginalString);

            using var again = await browser.GetAsync(DashboardFixture.DashboardPath);
            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            Assert.Equal(TestOidcProvider.AuthorizationEndpoint, again.Headers.Location!.GetLeftPart(UriPartial.Path));
        }
    }

    private async Task DeliverAsync(string body, string messageId)
    {
        using var request = GatewayRequests.PostReadings(body, _dashboard.Issuer.CreateToken(), messageId);
        using var response = await _dashboard.Factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
