using System.Net;
using IIoT.GatewayMock.Dashboard;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// An enabled dashboard that cannot sign anyone in, or that would fetch its trust over plain HTTP,
/// stops the mock from starting. Split into validator and host level for the reason given on
/// <see cref="GatewayMockStartupTests"/>.
/// </summary>
public class GatewayMockDashboardStartupTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();
    private readonly TestOidcProvider _provider = new();

    [Theory]
    [InlineData("GatewayMock:Dashboard:Authority", "http://login.iiot.test/", "Authority must be an absolute HTTPS URL")]
    [InlineData("GatewayMock:Dashboard:Authority", "login.iiot.test", "Authority must be an absolute HTTPS URL")]
    [InlineData("GatewayMock:Dashboard:ClientId", "", "ClientId is required")]
    [InlineData("GatewayMock:Dashboard:ClientId", " ", "ClientId is required")]
    [InlineData("GatewayMock:Dashboard:ClientSecret", "", "ClientSecret is required")]
    public void InvalidSetting_IsRejected(string setting, string value, string expected)
    {
        var settings = _provider.DashboardSettings();
        settings[setting] = value;

        Assert.Contains(expected, Validate(settings), StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection names the settings that are wrong, never their values: one of them is a secret,
    /// and startup failures end up in logs.
    /// </summary>
    [Fact]
    public void Rejection_NeverIncludesTheClientSecret()
    {
        var settings = _provider.DashboardSettings();
        settings["GatewayMock:Dashboard:Authority"] = "http://login.iiot.test/";
        settings["GatewayMock:Dashboard:ClientId"] = "";

        var failure = Validate(settings);

        Assert.NotNull(failure);
        Assert.DoesNotContain(_provider.ClientSecret, failure, StringComparison.Ordinal);
    }

    /// <summary>Positive control: each rejection above is caused by the one setting it changes.</summary>
    [Fact]
    public void ValidConfiguration_IsAccepted()
    {
        Assert.Null(Validate(_provider.DashboardSettings()));
    }

    /// <summary>Removing <c>ValidateOnStart</c> from the dashboard's registration makes this fail.</summary>
    [Fact]
    public async Task RejectedConfiguration_StopsStartup()
    {
        var settings = GatewayAndDashboardSettings();
        settings["GatewayMock:Dashboard:ClientSecret"] = "";

        await using var factory = new GatewayMockFactory(settings, services => DashboardFixture.StandInForTheProvider(services, _provider));

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }

    /// <summary>
    /// The authority is the switch. A client ID and secret without it leave the dashboard off, and
    /// the mock starts as the API alone.
    /// </summary>
    [Fact]
    public async Task SettingsWithoutAnAuthority_LeaveTheDashboardOff()
    {
        var settings = GatewayAndDashboardSettings();
        settings["GatewayMock:Dashboard:Authority"] = "";

        await using var factory = new GatewayMockFactory(settings);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DashboardFixture.DashboardPath);
        request.Headers.Authorization = new("Bearer", _issuer.CreateToken());

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _issuer.Dispose();
    }

    private Dictionary<string, string?> GatewayAndDashboardSettings()
    {
        var settings = _issuer.GatewaySettings();
        foreach (var (key, value) in _provider.DashboardSettings())
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>
    /// Binds the settings as the dashboard's registration does and runs its validator. Returns the
    /// failure message, or <c>null</c> when the configuration is accepted.
    /// </summary>
    private static string? Validate(Dictionary<string, string?> settings)
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(DashboardOptions.SectionName)
            .Get<DashboardOptions>() ?? new DashboardOptions();

        var result = new DashboardOptionsValidator().Validate(name: null, options);
        return result.Failed ? result.FailureMessage : null;
    }
}
