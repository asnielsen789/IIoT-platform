using System.Globalization;

namespace IIoT.GatewayMock.Dashboard;

/// <summary>
/// How the dashboard writes times and absent values, so every page writes them the same way.
/// </summary>
/// <remarks>
/// Times are shown in UTC, the one zone every clock involved agrees on: the device's, the
/// platform's and the mock's. A time that came in a body is shown formatted when it can be read,
/// and as sent when it cannot; the pages keep the value as sent in a tooltip either way.
/// </remarks>
internal static class DashboardFormat
{
    /// <summary>Shown for a value that is missing or empty.</summary>
    public const string Absent = "—";

    public static string Show(string? value) => string.IsNullOrEmpty(value) ? Absent : value;

    public static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>A time from a body, formatted when it can be read, otherwise as sent.</summary>
    public static string Instant(string? value) =>
        Parse(value) is { } instant ? Utc(instant) : Show(value);

    /// <summary>A short date and time for the chart's axis.</summary>
    public static string Axis(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>How long before <paramref name="now"/> something happened, roughly.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;

        // A device clock can be ahead; saying so is more useful than a negative age.
        if (elapsed < TimeSpan.FromMinutes(-1))
        {
            return "ahead of the mock's clock";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes} min ago";
        }

        return elapsed < TimeSpan.FromDays(2)
            ? $"{(int)elapsed.TotalHours} h ago"
            : $"{(int)elapsed.TotalDays} d ago";
    }

    /// <summary>As <see cref="Ago(DateTimeOffset, DateTimeOffset)"/>, for a time from a body; <c>null</c> when it cannot be read.</summary>
    public static string? Ago(string? value, DateTimeOffset now) =>
        Parse(value) is { } instant ? Ago(instant, now) : null;

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) ? instant : null;
}

/// <summary>
/// The dashboard's addresses. Device and message IDs travel as query parameters, escaped, never as
/// path segments.
/// </summary>
/// <remarks>
/// <para>
/// Both are untrusted and may hold any character within the contract's length limits, <c>/</c> and
/// <c>%</c> included. ASP.NET Core leaves <c>%2F</c> undecoded in a path, so an ID in a path segment
/// could not always be told apart from another; a query parameter decodes to exactly the ID.
/// </para>
/// <para>
/// A table's order travels the same way (<c>&amp;sort=…&amp;dir=…</c>), on the links that should
/// keep it: a column header, and on a device's page the way into a message and back out of it.
/// Without a sort, a link leads to the table's default order.
/// </para>
/// </remarks>
internal static class DashboardLinks
{
    public const string Overview = DashboardEndpoints.Route;

    /// <summary>The overview, with its devices in <paramref name="sort"/>'s order.</summary>
    public static string OverviewSorted(TableSort sort) => $"{Overview}?{Query(sort)}";

    public static string Device(string deviceId, TableSort? sort = null) =>
        $"{DashboardEndpoints.DeviceRoute}?id={Uri.EscapeDataString(deviceId)}{Then(sort)}";

    public static string Message(string deviceId, string messageId, TableSort? sort = null) =>
        $"{Device(deviceId)}&message={Uri.EscapeDataString(messageId)}{Then(sort)}";

    private static string Then(TableSort? sort) => sort is { } order ? "&" + Query(order) : "";

    private static string Query(TableSort sort) =>
        $"sort={Uri.EscapeDataString(sort.Key)}&dir={sort.Direction}";
}
