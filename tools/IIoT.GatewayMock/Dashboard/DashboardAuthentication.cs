using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// The dashboard's own authentication: an OpenID Connect sign-in that ends in a cookie session.
/// </summary>
/// <remarks>
/// <para>
/// Two schemes, neither of them the default. The bearer scheme stays the default, and every API
/// policy names it explicitly (<c>ApiAuthorization</c>), so the cookie can never authenticate an
/// API call. The dashboard's policy names only the cookie scheme, so the API's bearer token can
/// never open the dashboard either. Each side asks its own scheme and ignores the other's
/// credentials.
/// </para>
/// <para>
/// The cookie scheme forwards its challenge to the OpenID Connect scheme: a browser without a
/// session is redirected to the provider's sign-in page, and the provider's answer is turned into
/// the session cookie.
/// </para>
/// </remarks>
internal static class DashboardAuthentication
{
    /// <summary>The session after sign-in.</summary>
    public const string CookieScheme = "Dashboard.Cookie";

    /// <summary>The sign-in itself: the redirect to the provider and the code redemption after it.</summary>
    public const string OpenIdConnectScheme = "Dashboard.OpenIdConnect";

    /// <summary>
    /// Any person the provider signs in may look: the page is read-only and shows only what the
    /// mock received, so there is nothing to distinguish one viewer from another by. Who can sign
    /// in at all is the provider's decision (see the README on sign-ups).
    /// </summary>
    public static AuthorizationPolicy Policy { get; } =
        new AuthorizationPolicyBuilder(CookieScheme)
            .RequireAuthenticatedUser()
            .Build();

    /// <summary>
    /// Registers the dashboard's options, schemes and page rendering. Called only when
    /// <see cref="DashboardOptions.IsEnabled"/>; otherwise none of this exists.
    /// </summary>
    public static void AddDashboard(this IServiceCollection services, AuthenticationBuilder authentication)
    {
        services.AddOptions<DashboardOptions>()
            .BindConfiguration(DashboardOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>();

        authentication
            .AddCookie(CookieScheme)
            .AddOpenIdConnect(OpenIdConnectScheme, _ => { });

        // Registered after the schemes on purpose, as for the bearer scheme in Program.cs:
        // configure actions run in registration order, so these rules win.
        services.ConfigureOptions<ConfigureDashboardCookieOptions>();
        services.ConfigureOptions<ConfigureDashboardOpenIdConnectOptions>();

        // Static server-side rendering of one Razor component; see DashboardEndpoints.
        services.AddRazorComponents();
    }
}
