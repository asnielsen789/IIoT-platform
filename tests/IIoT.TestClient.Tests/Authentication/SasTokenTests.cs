using System.Net;
using IIoT.TestClient.Authentication;

namespace IIoT.TestClient.Tests.Authentication;

public class SasTokenTests
{
    /// <summary>
    /// Base64 of the ASCII text "PlaceholderKeyNotARealSecret1234567890". Not a key for anything;
    /// it exists so the signature below is a reproducible constant.
    /// </summary>
    private const string PlaceholderKey = "UGxhY2Vob2xkZXJLZXlOb3RBUmVhbFNlY3JldDEyMzQ1Njc4OTA=";

    private const string HostName = "example-hub.azure-devices.net";
    private const string DeviceId = "tank-000123";

    /// <summary>2026-01-01T00:00:00Z.</summary>
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(1767225600);

    [Fact]
    public void DeviceResourceUri_ScopesTheTokenToOneDevice()
    {
        // A device-scoped token must not be hub-scoped: a token for the hub root would authorise
        // publishing as any device, which is precisely the attack section 4 of the contract guards
        // against.
        Assert.Equal(
            "example-hub.azure-devices.net/devices/tank-000123",
            SasToken.DeviceResourceUri(HostName, DeviceId));
    }

    /// <summary>
    /// Known-value test. The expected signature was computed outside this codebase — openssl
    /// HMAC-SHA256 over <c>example-hub.azure-devices.net%2Fdevices%2Ftank-000123\n1767225600</c>
    /// with the base64-decoded key — so the test checks the implementation against Azure's
    /// documented algorithm rather than against itself.
    /// </summary>
    /// <remarks>
    /// Percent-escapes are uppercase. Microsoft's prose says "lower case URL-encoding", but all
    /// three of their reference implementations (WebUtility.UrlEncode, encodeURIComponent,
    /// quote_plus) emit uppercase, and it does not matter: the signature covers the encoded
    /// resource URI exactly as the <c>sr</c> field transmits it, so the hub recomputes over the
    /// same bytes whichever case is used. Following the reference implementations is the safer
    /// reading.
    /// </remarks>
    [Fact]
    public void Create_ProducesTheDocumentedToken()
    {
        var token = SasToken.Create(
            SasToken.DeviceResourceUri(HostName, DeviceId), PlaceholderKey, Expiry);

        Assert.Equal(
            "SharedAccessSignature " +
            "sr=example-hub.azure-devices.net%2Fdevices%2Ftank-000123" +
            "&sig=Ex7m06GHqds6Rcwm%2FWcZsYE92eGMffiBJEgXbBgY1vQ%3D" +
            "&se=1767225600",
            token);
    }

    [Fact]
    public void Create_SignsTheEncodedResourceUriAndExpiry()
    {
        var token = SasToken.Create(
            SasToken.DeviceResourceUri(HostName, DeviceId), PlaceholderKey, Expiry);

        // Decoded, so the assertion is about the signature and not about percent-encoding case.
        Assert.Equal("Ex7m06GHqds6Rcwm/WcZsYE92eGMffiBJEgXbBgY1vQ=", Field(token, "sig"));
        Assert.Equal("example-hub.azure-devices.net/devices/tank-000123", Field(token, "sr"));
    }

    [Fact]
    public void Create_OmitsThePolicyName()
    {
        var token = SasToken.Create(
            SasToken.DeviceResourceUri(HostName, DeviceId), PlaceholderKey, Expiry);

        // A token signed with a device identity's symmetric key carries no skn. Including one
        // would make IoT Hub look for a shared access policy of that name and reject the connect.
        Assert.DoesNotContain("skn=", token, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_IsDeterministic()
    {
        var resourceUri = SasToken.DeviceResourceUri(HostName, DeviceId);

        Assert.Equal(
            SasToken.Create(resourceUri, PlaceholderKey, Expiry),
            SasToken.Create(resourceUri, PlaceholderKey, Expiry));
    }

    [Fact]
    public void Create_ChangesWithTheExpiry()
    {
        var resourceUri = SasToken.DeviceResourceUri(HostName, DeviceId);

        Assert.NotEqual(
            SasToken.Create(resourceUri, PlaceholderKey, Expiry),
            SasToken.Create(resourceUri, PlaceholderKey, Expiry.AddSeconds(1)));
    }

    [Fact]
    public void Create_RejectsAKeyThatIsNotBase64()
    {
        // The usual mistake is pasting a whole connection string. The message must say so without
        // echoing what was pasted.
        var exception = Assert.Throws<FormatException>(() => SasToken.Create(
            SasToken.DeviceResourceUri(HostName, DeviceId),
            "HostName=example;DeviceId=tank-000123;SharedAccessKey=not-base-64!",
            Expiry));

        Assert.Contains("connection string", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-base-64", exception.Message, StringComparison.Ordinal);
    }

    private static string Field(string token, string name)
    {
        var fields = token[SasToken.Prefix.Length..].Split('&');
        var field = fields.Single(f => f.StartsWith(name + "=", StringComparison.Ordinal));

        return WebUtility.UrlDecode(field[(name.Length + 1)..]);
    }
}
