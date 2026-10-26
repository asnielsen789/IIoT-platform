namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// How people sign in to the dashboard. Bound from <c>GatewayMock:Dashboard</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard is opt-in, and <see cref="Authority"/> is the switch. Without it, nothing of the
/// dashboard is registered or mapped, and the mock is exactly the API it is without one: the hosted
/// mock can be deployed first and given a dashboard later by configuration alone.
/// </para>
/// <para>
/// A browser cannot present the API's bearer token, so a person signs in with OpenID Connect
/// instead: the authorization code flow with PKCE, against any provider that publishes a discovery
/// document. Nothing here names a provider; in the reference deployment it is the same Auth0 tenant
/// that issues the API's tokens, through a separate application of the "Regular Web Application"
/// kind.
/// </para>
/// <para>
/// Unlike <c>GatewayMock:Jwt</c>, this section holds a secret: <see cref="ClientSecret"/>.
/// </para>
/// </remarks>
public sealed class DashboardOptions
{
    public const string SectionName = "GatewayMock:Dashboard";

    /// <summary>
    /// The OpenID Connect issuer people sign in with — for Auth0,
    /// <c>https://&lt;tenant&gt;.&lt;region&gt;.auth0.com/</c>. Setting it enables the dashboard.
    /// Must be HTTPS: the sign-in page, the token endpoint and the keys that verify the ID token all
    /// come from its discovery document.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>The dashboard's client ID at the provider. Required when the dashboard is enabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The dashboard's client secret, which it presents when it redeems an authorization code.
    /// Required when the dashboard is enabled. <b>Secret:</b> supplied by the host — an environment
    /// variable, or an app setting that is a Key Vault reference — never in a committed file, and
    /// never logged.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Whether <paramref name="configuration"/> enables the dashboard. Decided before the app is
    /// built, because it decides what is registered.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{nameof(Authority)}"]);
}
