using System.Net;
using IIoT.GatewayMock.Dashboard;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// The mock with its dashboard enabled, the token issuer its API trusts and the OpenID Connect
/// provider its dashboard signs people in with. Shared by the tests of a class; each test signs in
/// with a browser of its own, so no test depends on another's session.
/// </summary>
public sealed class DashboardFixture : IDisposable
{
    /// <summary>
    /// The dashboard's paths, spelled out rather than borrowed from the mock: they are what an
    /// operator registers at the provider, and a test that reused the implementation's constants
    /// would follow a change to them instead of catching it.
    /// </summary>
    public const string DashboardPath = "/dashboard";
    public const string SignOutPath = "/dashboard/sign-out";
    public const string CallbackPath = "/dashboard/signin-oidc";
    public const string SignedOutCallbackPath = "/dashboard/signout-callback-oidc";
    public const string SessionCookieName = "IIoT.GatewayMock.Dashboard";

    /// <summary>
    /// The network the fixture's mock believes <c>X-Forwarded-Proto</c> from: where its
    /// TLS-terminating proxy would connect from.
    /// </summary>
    public const string TrustedProxyNetwork = "10.0.0.0/8";

    public DashboardFixture()
    {
        Issuer = new TestTokenIssuer();
        Provider = new TestOidcProvider();

        var settings = Issuer.GatewaySettings();
        foreach (var (key, value) in Provider.DashboardSettings())
        {
            settings[key] = value;
        }

        settings["GatewayMock:ForwardedHeaders:KnownNetworks:0"] = TrustedProxyNetwork;

        Factory = new GatewayMockFactory(settings, services => StandInForTheProvider(services, Provider));
    }

    public TestTokenIssuer Issuer { get; }

    public TestOidcProvider Provider { get; }

    public GatewayMockFactory Factory { get; }

    /// <summary>
    /// What a test replaces so the dashboard never leaves the process: the provider's network
    /// endpoints, and the data-protection keys that protect the session cookie and the sign-in
    /// state, which would otherwise be written to the user profile.
    /// </summary>
    public static void StandInForTheProvider(IServiceCollection services, TestOidcProvider provider)
    {
        services.Configure<OpenIdConnectOptions>(
            DashboardAuthentication.OpenIdConnectScheme,
            options => options.BackchannelHttpHandler = provider.CreateBackchannelHandler());

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
    }

    /// <summary>
    /// A client that behaves like a browser: it keeps cookies, does not follow redirects on its own
    /// (a redirect to the provider leaves the test server), and talks HTTPS, as the hosted mock does.
    /// </summary>
    public HttpClient CreateBrowser() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>
    /// Opens the dashboard in a new browser and signs in through the provider, as a person would.
    /// </summary>
    /// <returns>The signed-in browser, and the session cookie it was given, as a <c>Cookie</c> header value.</returns>
    public async Task<(HttpClient Browser, string SessionCookie)> SignInAsync(string userName = TestOidcProvider.DefaultUserName)
    {
        var browser = CreateBrowser();

        using var challenge = await browser.GetAsync(DashboardPath);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);

        var response = Provider.SignIn(challenge.Headers.Location!, userName);
        using var form = response.ToForm();
        using var callback = await browser.PostAsync(response.RedirectUri, form);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(DashboardPath, callback.Headers.Location?.OriginalString);

        var sessionCookie = Assert.Single(
            callback.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(SessionCookieName + "=", StringComparison.Ordinal));

        return (browser, sessionCookie.Split(';')[0]);
    }

    public void Dispose()
    {
        Factory.Dispose();
        Provider.Dispose();
        Issuer.Dispose();
    }
}
