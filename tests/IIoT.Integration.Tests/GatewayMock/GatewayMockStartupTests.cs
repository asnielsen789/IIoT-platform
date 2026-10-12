using System.Security.Cryptography;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// A configuration that cannot authenticate anything, or that would authenticate too much,
/// stops the mock from starting rather than producing a gateway that quietly accepts any token.
/// </summary>
public class GatewayMockStartupTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    [Theory]
    [InlineData("GatewayMock:Jwt:Audience", "Audience is required")]
    [InlineData("GatewayMock:Jwt:Issuer", "Issuer is required")]
    public async Task MissingSetting_StopsStartup(string setting, string expected)
    {
        var settings = _issuer.GatewaySettings();
        settings[setting] = "";

        var failure = await StartAsync(settings);

        Assert.Contains(expected, failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAuthorityAndNoSigningKeys_StopsStartup()
    {
        var settings = _issuer.GatewaySettings();
        settings.Remove("GatewayMock:Jwt:SigningKeys:0");

        var failure = await StartAsync(settings);

        Assert.Contains("no key to verify a signature with", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainHttpAuthority_StopsStartup()
    {
        var settings = _issuer.GatewaySettings();
        settings["GatewayMock:Jwt:Authority"] = "http://token-issuer.iiot.test/";

        var failure = await StartAsync(settings);

        Assert.Contains("Authority must be an absolute HTTPS URL", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A private key would verify signatures just as well, which is exactly why it must be
    /// refused: it is a secret, and pasting it into configuration is the mistake to catch.
    /// </summary>
    [Fact]
    public async Task PrivateKeyAsSigningKey_StopsStartup()
    {
        using var rsa = RSA.Create(2048);
        var settings = _issuer.GatewaySettings();
        settings["GatewayMock:Jwt:SigningKeys:0"] = rsa.ExportPkcs8PrivateKeyPem();

        var failure = await StartAsync(settings);

        Assert.Contains("not a public key", failure, StringComparison.Ordinal);
    }

    public void Dispose() => _issuer.Dispose();

    /// <summary>Starts a mock that is expected to refuse, and returns why it refused.</summary>
    private static async Task<string> StartAsync(Dictionary<string, string?> settings)
    {
        await using var factory = new GatewayMockFactory(settings);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var validation = FindValidationFailure(exception!);
        Assert.True(validation is not null, $"expected an options validation failure, got: {exception}");
        return validation!.Message;
    }

    private static OptionsValidationException? FindValidationFailure(Exception exception) =>
        exception switch
        {
            OptionsValidationException validation => validation,
            AggregateException aggregate => aggregate.InnerExceptions.Select(FindValidationFailure).FirstOrDefault(found => found is not null),
            { InnerException: { } inner } => FindValidationFailure(inner),
            _ => null
        };
}
