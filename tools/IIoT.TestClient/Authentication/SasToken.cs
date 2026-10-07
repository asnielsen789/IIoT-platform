using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace IIoT.TestClient.Authentication;

/// <summary>
/// Generates the shared access signature IoT Hub expects in the MQTT CONNECT password field.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is fixed by Azure, not by us: HMAC-SHA256 over
/// <c>{url-encoded resource URI} + "\n" + {expiry}</c>, signed with the base64-decoded device key.
/// A token scoped to a device identity's symmetric key carries no <c>skn</c> policy name.
/// Documented at
/// https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-sas#sas-token-structure.
/// </para>
/// <para>
/// <see cref="WebUtility.UrlEncode"/> emits uppercase percent-escapes. Microsoft's prose asks for
/// lowercase, but their own C#, Node and Python samples all emit uppercase, and the case is
/// immaterial: the signature covers the encoded resource URI exactly as the <c>sr</c> field
/// transmits it, so the hub recomputes over the same bytes either way.
/// </para>
/// </remarks>
public static class SasToken
{
    /// <summary>The literal prefix of the token, including the trailing space.</summary>
    public const string Prefix = "SharedAccessSignature ";

    /// <summary>
    /// The resource URI a device-scoped token must be bound to. Casing is preserved: IoT Hub
    /// device ids are case-sensitive, and the Azure reference implementation does not lower-case
    /// this value either.
    /// </summary>
    public static string DeviceResourceUri(string hostName, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return $"{hostName}/devices/{deviceId}";
    }

    /// <summary>
    /// Creates a token for <paramref name="resourceUri"/> valid until <paramref name="expiresAt"/>.
    /// </summary>
    /// <param name="resourceUri">Unencoded resource URI, from <see cref="DeviceResourceUri"/>.</param>
    /// <param name="base64Key">The device's symmetric key, exactly as IoT Hub reports it.</param>
    /// <param name="expiresAt">Expiry. Truncated to whole seconds, as the format demands.</param>
    public static string Create(string resourceUri, string base64Key, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            // Worth catching explicitly: the most common mistake is pasting a whole connection
            // string, or a device id, where the key belongs. The raw key must never be echoed.
            throw new FormatException(
                "The device key is not valid base64. Supply only the SharedAccessKey value, " +
                "not the full connection string.", ex);
        }

        var expiry = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var encodedResourceUri = WebUtility.UrlEncode(resourceUri);
        var stringToSign = $"{encodedResourceUri}\n{expiry}";

        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));

        // Field order matches the documented structure: sr, sig, se (and skn, which a
        // device-key token omits).
        return $"{Prefix}sr={encodedResourceUri}" +
               $"&sig={WebUtility.UrlEncode(signature)}" +
               $"&se={expiry}";
    }
}
