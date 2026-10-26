namespace IIoT.GatewayMock.Hosting;

/// <summary>
/// Whether, and from whom, the mock believes <c>X-Forwarded-Proto</c>. Bound from
/// <c>GatewayMock:ForwardedHeaders</c>.
/// </summary>
/// <remarks>
/// <para>
/// Behind a TLS-terminating proxy, such as the front end of Azure App Service, every request
/// reaches the app as plain HTTP. The dashboard's sign-in builds its redirect URI from the
/// request's scheme, so without the forwarded scheme it would ask the provider to send the user
/// back to <c>http://</c>, which the provider refuses, and rightly so. The API does not care.
/// </para>
/// <para>
/// A forwarded header is only as trustworthy as whoever added it. The header is believed only from
/// the loopback addresses (the framework's default) and the networks listed in
/// <see cref="KnownNetworks"/>; from any other address it is ignored. Only the scheme is taken:
/// not <c>X-Forwarded-Host</c>, which would let a forged header choose the host the provider sends
/// the authorization code to, and not <c>X-Forwarded-For</c>, which nothing here uses.
/// </para>
/// </remarks>
public sealed class GatewayForwardedHeadersOptions
{
    public const string SectionName = "GatewayMock:ForwardedHeaders";

    /// <summary>
    /// <c>true</c> or <c>false</c> to decide explicitly. Unset, the forwarded scheme is used when
    /// the dashboard is enabled, which is the only part of the mock that needs it.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Networks, in CIDR notation (<c>10.0.0.0/8</c>), from which the forwarded scheme is believed,
    /// in addition to loopback. For App Service, the private ranges its front end connects from.
    /// A single proxy is a <c>/32</c>. <c>0.0.0.0/0</c> would believe anyone who can reach the app.
    /// </summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>Whether the forwarded scheme is used, given whether the dashboard is enabled.</summary>
    public bool IsEnabled(bool dashboardEnabled) => Enabled ?? dashboardEnabled;
}
