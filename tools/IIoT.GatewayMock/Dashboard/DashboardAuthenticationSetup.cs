using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// The dashboard's session cookie.
/// </summary>
internal sealed class ConfigureDashboardCookieOptions : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public const string CookieName = "IIoT.GatewayMock.Dashboard";

    /// <summary>
    /// How long a sign-in lasts, absolutely. The page refreshes itself, so a sliding expiration
    /// would never expire while it is open; an absolute one sends the browser back to the provider
    /// at least this often, which is when a user the provider has since blocked is turned away.
    /// While the provider's own session lasts, that round trip needs no input from the user.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (name != DashboardAuthentication.CookieScheme)
        {
            return;
        }

        options.Cookie.Name = CookieName;

        // Scoped to the dashboard's paths, so a browser never even attaches it to an API request.
        // The API refuses it regardless (ApiAuthorization); this keeps it out of those requests.
        options.Cookie.Path = DashboardEndpoints.Route;

        // HTTPS only, whatever scheme the request arrived with: behind a TLS-terminating proxy the
        // app sees plain HTTP, and "same as request" would issue the session cookie without Secure.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;

        // Lax, not Strict: the sign-in ends with a redirect chain that starts at the provider, and
        // a Strict cookie would not be sent on its last step, which would start the sign-in again.
        options.Cookie.SameSite = SameSiteMode.Lax;

        options.ExpireTimeSpan = SessionLifetime;
        options.SlidingExpiration = false;

        // No login page of its own: a browser without a session goes to the provider.
        options.ForwardChallenge = DashboardAuthentication.OpenIdConnectScheme;
    }
}

/// <summary>
/// Turns <see cref="DashboardOptions"/> into the OpenID Connect handler's settings.
/// </summary>
/// <remarks>
/// As for the bearer scheme, the security-relevant settings are stated even where they match the
/// framework's default, so that a reader does not have to know the default, and so that the one
/// default that is wrong for this app — the response type — cannot hide among them.
/// </remarks>
internal sealed class ConfigureDashboardOpenIdConnectOptions : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string CallbackPath = DashboardEndpoints.Route + "/signin-oidc";
    public const string SignedOutCallbackPath = DashboardEndpoints.Route + "/signout-callback-oidc";
    public const string RemoteSignOutPath = DashboardEndpoints.Route + "/signout-oidc";

    private readonly IOptions<DashboardOptions> _dashboardOptions;

    public ConfigureDashboardOpenIdConnectOptions(IOptions<DashboardOptions> dashboardOptions)
    {
        _dashboardOptions = dashboardOptions;
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != DashboardAuthentication.OpenIdConnectScheme)
        {
            return;
        }

        var dashboard = _dashboardOptions.Value;

        // The provider's endpoints and signing keys come from its discovery document.
        // RequireHttpsMetadata stays at its default of true, and the validator insists on HTTPS.
        options.Authority = dashboard.Authority;
        options.ClientId = dashboard.ClientId;
        options.ClientSecret = dashboard.ClientSecret;

        // The authorization code flow. The handler's default is still "id_token" — the implicit
        // flow, which hands the token to the browser in the redirect — so this one is not optional.
        // The code is redeemed server to server, with the client secret and the PKCE verifier.
        options.ResponseType = OpenIdConnectResponseType.Code;

        // PKCE (RFC 7636). The handler's default is true, but it applies only to the code flow:
        // stated so it is visibly on. A stolen authorization code is useless without the verifier,
        // which never leaves this server.
        options.UsePkce = true;

        // The provider posts the code back in a form rather than in the URL, so it never lands in
        // browser history, proxy logs or a Referer header. Also the handler's default.
        options.ResponseMode = OpenIdConnectResponseMode.FormPost;

        // Who the user is, and nothing more. The page calls no API on the user's behalf.
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");

        options.SignInScheme = DashboardAuthentication.CookieScheme;
        options.SignOutScheme = DashboardAuthentication.CookieScheme;

        // Under the dashboard's own path, so that everything the dashboard adds is in one place —
        // and is all absent when it is off.
        options.CallbackPath = CallbackPath;
        options.SignedOutCallbackPath = SignedOutCallbackPath;
        options.RemoteSignOutPath = RemoteSignOutPath;
        options.SignedOutRedirectUri = DashboardEndpoints.Route;

        // The provider has no need to learn which library and version signs people in here.
        options.DisableTelemetry = true;

        // The session needs nothing from the tokens once the user is signed in. Not saving them
        // keeps the ID token and the access token out of the cookie.
        options.SaveTokens = false;

        // Keep claim names as the provider sends them (sub, name), as for the bearer scheme.
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";

        // RS256 only, as for the API's tokens. A provider can also sign ID tokens with HS256, using
        // the client secret as the key; that would make the secret a signing key as well.
        options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];

        // Sign-out ends the provider's session too (OpenID Connect RP-Initiated Logout). With no
        // ID token saved there is no id_token_hint to send, so the request identifies the client
        // by client_id, which the specification allows and which a provider needs before it will
        // redirect back to the post-logout URI.
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = context.Options.ClientId;
            return Task.CompletedTask;
        };
    }
}

/// <summary>
/// Refuses to start the mock with a dashboard that cannot sign anyone in, or that would fetch its
/// trust from an unauthenticated source.
/// </summary>
/// <remarks>
/// Runs only when the dashboard is enabled, which is when <see cref="DashboardOptions.Authority"/>
/// is set. No message includes a configured value: one of them is a secret.
/// </remarks>
internal sealed class DashboardOptionsValidator : IValidateOptions<DashboardOptions>
{
    public ValidateOptionsResult Validate(string? name, DashboardOptions options)
    {
        const string Section = DashboardOptions.SectionName;
        var failures = new List<string>();

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            authority.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{Section}:Authority must be an absolute HTTPS URL; the sign-in page, the token endpoint and the keys that verify the ID token all come from it.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add($"{Section}:ClientId is required when the dashboard is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            failures.Add(
                $"{Section}:ClientSecret is required when the dashboard is enabled. Supply it from the host — an environment variable, or an app setting that is a Key Vault reference — never from a committed file.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
