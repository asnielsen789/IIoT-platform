using System.Security.Claims;
using IIoT.GatewayMock.Readings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// The read-only dashboard of what the mock has received: <c>GET /dashboard</c>, the overview;
/// <c>GET /dashboard/device?id=…</c>, one device's history, with <c>&amp;message=…</c> for a
/// message's raw body over it; and <c>GET /dashboard/sign-out</c>. Mapped only when the dashboard
/// is enabled.
/// </summary>
/// <remarks>
/// <para>
/// Minimal-API endpoints that render a Razor component (<see cref="OverviewPage"/>,
/// <see cref="DevicePage"/>) on the server, rather than Razor Pages. The component gives the page Razor's encoding — every value is
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
    public const string DeviceRoute = Route + "/device";
    public const string SignOutRoute = Route + "/sign-out";

    /// <summary>
    /// Often enough to watch a reading arrive during a demonstration; each refresh is one small
    /// in-memory read.
    /// </summary>
    public const int RefreshSeconds = 10;

    /// <summary>
    /// No script, no external resources, no framing, no form targets. The pages need only their
    /// own inline stylesheet; their graphics are inline SVG. If an encoding gap ever let markup
    /// through, the browser would still refuse to run a script.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static RouteGroupBuilder MapDashboard(this IEndpointRouteBuilder endpoints)
    {
        // The cookie scheme only. The API's bearer token does not open the dashboard, just as the
        // dashboard's cookie does not authenticate an API call.
        var dashboard = endpoints.MapGroup(Route).RequireAuthorization(DashboardAuthentication.Policy);

        dashboard.MapGet("", RenderOverview);
        dashboard.MapGet("/device", RenderDevice);
        dashboard.MapGet("/sign-out", SignOut);

        return dashboard;
    }

    private static RazorComponentResult<OverviewPage> RenderOverview(
        HttpResponse response,
        ClaimsPrincipal user,
        ReadingStore store,
        TimeProvider timeProvider)
    {
        Secure(response);

        return new RazorComponentResult<OverviewPage>(new Dictionary<string, object?>
        {
            [nameof(OverviewPage.Snapshot)] = DashboardSnapshot.Create(store.Batches, timeProvider.GetUtcNow()),
            [nameof(OverviewPage.SignedInAs)] = SignedInAs(user),
            [nameof(OverviewPage.RefreshSeconds)] = RefreshSeconds,
            [nameof(OverviewPage.SignOutPath)] = SignOutRoute
        });
    }

    /// <summary>
    /// One device, and with <paramref name="message"/> one of its messages over it. A device or
    /// message the mock does not hold is a 404 page with a way back.
    /// </summary>
    /// <param name="id">The device ID, unescaped from the query string. Untrusted.</param>
    /// <param name="message">The message ID, or absent. Untrusted.</param>
    private static IResult RenderDevice(
        HttpResponse response,
        ClaimsPrincipal user,
        ReadingStore store,
        TimeProvider timeProvider,
        string? id,
        string? message)
    {
        Secure(response);
        var now = timeProvider.GetUtcNow();

        var snapshot = string.IsNullOrEmpty(id)
            ? null
            : DeviceSnapshot.Create(store.Batches, id, string.IsNullOrEmpty(message) ? null : message, now);

        if (snapshot is null)
        {
            return new RazorComponentResult<NotFoundPage>(new Dictionary<string, object?>
            {
                [nameof(NotFoundPage.What)] = string.IsNullOrEmpty(message)
                    ? "The mock holds no messages from this device."
                    : "The mock holds no such message from this device.",
                [nameof(NotFoundPage.SignedInAs)] = SignedInAs(user),
                [nameof(NotFoundPage.SignOutPath)] = SignOutRoute,
                [nameof(NotFoundPage.TakenAt)] = now
            })
            {
                StatusCode = StatusCodes.Status404NotFound
            };
        }

        return new RazorComponentResult<DevicePage>(new Dictionary<string, object?>
        {
            [nameof(DevicePage.Snapshot)] = snapshot,
            [nameof(DevicePage.SignedInAs)] = SignedInAs(user),
            [nameof(DevicePage.RefreshSeconds)] = RefreshSeconds,
            [nameof(DevicePage.SignOutPath)] = SignOutRoute
        });
    }

    private static void Secure(HttpResponse response)
    {
        var headers = response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";

        // What the pages show is only for the signed-in viewer, and is stale within seconds.
        headers.CacheControl = "no-store";
    }

    /// <summary>The signed-in user's name, as the provider sent it.</summary>
    private static string? SignedInAs(ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirst("sub")?.Value;

    /// <summary>
    /// Ends the session here and then at the provider, which redirects back to the dashboard,
    /// where the next visit starts a new sign-in.
    /// </summary>
    private static SignOutHttpResult SignOut() =>
        TypedResults.SignOut(
            new AuthenticationProperties { RedirectUri = Route },
            [DashboardAuthentication.CookieScheme, DashboardAuthentication.OpenIdConnectScheme]);
}
