using System.Security.Cryptography;
using IIoT.GatewayMock.Authentication;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// A configuration that cannot authenticate anything, or that would authenticate too much,
/// stops the mock from starting rather than producing a gateway that quietly accepts any token.
/// </summary>
/// <remarks>
/// Two layers, tested separately. The reasons are tested against the validator itself, bound from
/// configuration the same way <c>Program.cs</c> binds it. That a rejected configuration actually
/// stops the host is tested once, at host level, by asserting only that startup fails.
///
/// The split is deliberate. With minimal hosting, the mock's own entry point runs <c>app.Run()</c>
/// on a background thread, and <c>WebApplicationFactory</c> can touch the host after that thread
/// has already disposed it. The original <c>OptionsValidationException</c> then surfaces as an
/// <c>ObjectDisposedException</c>, depending on timing; this happened on the Linux CI runner and
/// not locally. Asserting the reason through a host is therefore racy. Asserting that startup
/// fails is not, because some exception always surfaces.
/// </remarks>
public class GatewayMockStartupTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    [Theory]
    [InlineData("GatewayMock:Jwt:Audience", "Audience is required")]
    [InlineData("GatewayMock:Jwt:Issuer", "Issuer is required")]
    public void MissingSetting_IsRejected(string setting, string expected)
    {
        var settings = _issuer.GatewaySettings();
        settings[setting] = "";

        Assert.Contains(expected, Validate(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void NoAuthorityAndNoSigningKeys_IsRejected()
    {
        var settings = _issuer.GatewaySettings();
        settings.Remove("GatewayMock:Jwt:SigningKeys:0");

        Assert.Contains("no key to verify a signature with", Validate(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void PlainHttpAuthority_IsRejected()
    {
        var settings = _issuer.GatewaySettings();
        settings["GatewayMock:Jwt:Authority"] = "http://token-issuer.iiot.test/";

        Assert.Contains("Authority must be an absolute HTTPS URL", Validate(settings), StringComparison.Ordinal);
    }

    /// <summary>
    /// A private key would verify signatures just as well, which is exactly why it must be
    /// refused: it is a secret, and pasting it into configuration is the mistake to catch.
    /// </summary>
    [Fact]
    public void PrivateKeyAsSigningKey_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var settings = _issuer.GatewaySettings();
        settings["GatewayMock:Jwt:SigningKeys:0"] = rsa.ExportPkcs8PrivateKeyPem();

        Assert.Contains("not a public key", Validate(settings), StringComparison.Ordinal);
    }

    /// <summary>
    /// Positive control for the tests above: the configuration every other test starts the mock
    /// with passes validation, so each rejection is caused by the one setting it changes.
    /// </summary>
    [Fact]
    public void ValidConfiguration_IsAccepted()
    {
        Assert.Null(Validate(_issuer.GatewaySettings()));
    }

    /// <summary>
    /// The validator only matters if it runs before the mock accepts requests. A rejected
    /// configuration must stop the host from starting: removing <c>ValidateOnStart</c> from
    /// <c>Program.cs</c> makes this test fail.
    /// </summary>
    [Fact]
    public async Task RejectedConfiguration_StopsStartup()
    {
        var settings = _issuer.GatewaySettings();
        settings["GatewayMock:Jwt:Audience"] = "";

        await using var factory = new GatewayMockFactory(settings);

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }

    public void Dispose() => _issuer.Dispose();

    /// <summary>
    /// Binds the settings as <c>Program.cs</c> does and runs the mock's validator. Returns the
    /// failure message, or <c>null</c> when the configuration is accepted.
    /// </summary>
    private static string? Validate(Dictionary<string, string?> settings)
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(GatewayJwtOptions.SectionName)
            .Get<GatewayJwtOptions>() ?? new GatewayJwtOptions();

        var result = new GatewayJwtOptionsValidator().Validate(name: null, options);
        return result.Failed ? result.FailureMessage : null;
    }
}
