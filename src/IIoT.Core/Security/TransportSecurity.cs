using System.Net;

namespace IIoT.Core.Security;

/// <summary>
/// The platform's one rule for outbound HTTP: HTTPS, or a loopback address.
/// </summary>
/// <remarks>
/// <para>
/// Everything the platform sends over HTTP carries something worth protecting — a client secret
/// to the token endpoint, a bearer token and telemetry to the gateway. A plain-HTTP address
/// would put all of it on the wire in clear text, so one is refused before anything is sent
/// rather than trusted to a redirect afterwards.
/// </para>
/// <para>
/// Loopback is the only exception: traffic to <c>localhost</c>, <c>127.0.0.0/8</c> or
/// <c>::1</c> never leaves the machine, and in-process tests need it. The check is written out
/// here rather than delegated to <see cref="Uri.IsLoopback"/> so that the whole rule can be read
/// where it is enforced. Names that merely start with <c>localhost</c>, such as
/// <c>localhost.example.com</c>, are not loopback and are refused.
/// </para>
/// </remarks>
public static class TransportSecurity
{
    /// <summary>Whether <paramref name="uri"/> may be used for an outbound call.</summary>
    public static bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri);
    }

    /// <summary>
    /// Why <paramref name="uri"/> cannot be used, or <c>null</c> if it can. Names the setting so
    /// the reason is actionable when it appears in a log.
    /// </summary>
    public static string? DescribeViolation(Uri? uri, string settingName)
    {
        if (uri is null)
        {
            return $"{settingName} is not configured";
        }

        if (!uri.IsAbsoluteUri)
        {
            return $"{settingName} must be an absolute URL";
        }

        return IsAllowed(uri)
            ? null
            : $"{settingName} must use https; refusing to send to {uri.Scheme}://{uri.Authority} " +
              "because plain HTTP is allowed only to a loopback address";
    }

    private static bool IsLoopback(Uri uri) =>
        uri.HostNameType switch
        {
            UriHostNameType.Dns => string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase),
            UriHostNameType.IPv4 or UriHostNameType.IPv6 =>
                IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address),
            _ => false
        };
}
