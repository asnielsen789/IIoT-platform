using System.Security.Claims;
using IIoT.GatewayMock.Readings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// <c>GET /dashboard</c> — a read-only view of what the mock has received — and
/// <c>GET /dashboard/sign-out</c>. Mapped only when the dashboard is enabled.
/// </summary>
/// <remarks>
/// <para>
/// Minimal-API endpoints that render one Razor component (<see cref="DashboardPage"/>) on the
/// server, rather than Razor Pages. The component gives the page Razor's encoding — every value is
/// HTML-encoded unless deliberately marked as markup — without bringing in MVC for a single page,
/// and the endpoints are mapped and protected exactly like <c>POST /v1/readings</c>, in code a
/// reader can follow. Nothing is interactive and no script is sent: the page refreshes itself with
/// a <c>meta</c> refresh.
/// </para>
/// <para>
/// There are no forms and no actions that change anything. Sign-out is a plain link: a request
/// forged from another site could at worst sign the viewer out, and it changes no data.
/// </para>
/// </remarks>
internal static class DashboardEndpoints
{
    public const string Route = "/dashboard";
    public const string SignOutRoute = Route + "/sign-out";

    /// <summary>
    /// Often enough to watch a reading arrive during a demonstration; each refresh is one small
    /// in-memory read.
    /// </summary>
    public const int RefreshSeconds = 10;

    /// <summary>
    /// No script, no external resources, no framing, no form targets. The page needs only its own
    /// inline stylesheet. If an encoding gap ever let markup through, the browser would still
    /// refuse to run a script.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static RouteGroupBuilder MapDashboard(this IEndpointRouteBuilder endpoints)
    {
        // The cookie scheme only. The API's bearer token does not open the dashboard, just as the
        // dashboard's cookie does not authenticate an API call.
        var dashboard = endpoints.MapGroup(Route).RequireAuthorization(DashboardAuthentication.Policy);

        dashboard.MapGet("", Render);
        dashboard.MapGet("/sign-out", SignOut);

        return dashboard;
    }

    private static RazorComponentResult<DashboardPage> Render(
        HttpResponse response,
        ClaimsPrincipal user,
        ReadingStore store,
        TimeProvider timeProvider)
    {
        var snapshot = DashboardSnapshot.Create(store.Batches, timeProvider.GetUtcNow());

        var headers = response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";

        // What the page shows is only for the signed-in viewer, and is stale within seconds.
        headers.CacheControl = "no-store";

        return new RazorComponentResult<DashboardPage>(new Dictionary<string, object?>
        {
            [nameof(DashboardPage.Snapshot)] = snapshot,
            [nameof(DashboardPage.SignedInAs)] = user.Identity?.Name ?? user.FindFirst("sub")?.Value,
            [nameof(DashboardPage.RefreshSeconds)] = RefreshSeconds,
            [nameof(DashboardPage.SignOutPath)] = SignOutRoute
        });
    }

    /// <summary>
    /// Ends the session here and then at the provider, which redirects back to the dashboard,
    /// where the next visit starts a new sign-in.
    /// </summary>
    private static SignOutHttpResult SignOut() =>
        TypedResults.SignOut(
            new AuthenticationProperties { RedirectUri = Route },
            [DashboardAuthentication.CookieScheme, DashboardAuthentication.OpenIdConnectScheme]);
}
