using IIoT.GatewayMock.Hosting;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// When the forwarded scheme is used, and a trusted network that cannot be read stops the mock
/// from starting. Validator and host level apart, for the reason given on
/// <see cref="GatewayMockStartupTests"/>.
/// </summary>
public class GatewayMockForwardedHeadersStartupTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();
    private readonly TestOidcProvider _provider = new();

    /// <summary>
    /// Off unless the dashboard, the only part that needs it, is on — and an explicit setting
    /// wins either way.
    /// </summary>
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void IsEnabled_FollowsTheDashboard_UnlessSetExplicitly(bool? enabled, bool dashboardEnabled, bool expected)
    {
        var options = new GatewayForwardedHeadersOptions { Enabled = enabled };

        Assert.Equal(expected, options.IsEnabled(dashboardEnabled));
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.0.2.17/32")]
    [InlineData("fd00::/8")]
    public void CidrNetwork_IsAccepted(string network)
    {
        Assert.Null(Validate(network));
    }

    /// <summary>A bare address is refused too: a single proxy is written as a /32.</summary>
    [Theory]
    [InlineData("not-a-network")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.20.30.40")]
    [InlineData("")]
    public void UnreadableNetwork_IsRejected(string network)
    {
        Assert.Contains("is not a network in CIDR notation", Validate(network), StringComparison.Ordinal);
    }

    /// <summary>Removing <c>ValidateOnStart</c> from the forwarded-headers registration makes this fail.</summary>
    [Fact]
    public async Task RejectedNetwork_StopsStartup()
    {
        var settings = _issuer.GatewaySettings();
        foreach (var (key, value) in _provider.DashboardSettings())
        {
            settings[key] = value;
        }

        settings["GatewayMock:ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/33";

        await using var factory = new GatewayMockFactory(settings, services => DashboardFixture.StandInForTheProvider(services, _provider));

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _issuer.Dispose();
    }

    private static string? Validate(string network)
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewayMock:ForwardedHeaders:KnownNetworks:0"] = network
            })
            .Build()
            .GetSection(GatewayForwardedHeadersOptions.SectionName)
            .Get<GatewayForwardedHeadersOptions>() ?? new GatewayForwardedHeadersOptions();

        var result = new GatewayForwardedHeadersOptionsValidator().Validate(name: null, options);
        return result.Failed ? result.FailureMessage : null;
    }
}
