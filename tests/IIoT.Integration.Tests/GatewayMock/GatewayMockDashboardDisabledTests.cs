using System.Net;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// Without <c>GatewayMock:Dashboard:Authority</c> the dashboard does not exist: no routes, no
/// sign-in, no cookie scheme. The mock is the API alone, which is how it is first deployed. Every
/// other API test runs in this configuration as well.
/// </summary>
public class GatewayMockDashboardDisabledTests : IClassFixture<GatewayMockFixture>
{
    private readonly GatewayMockFixture _gateway;

    public GatewayMockDashboardDisabledTests(GatewayMockFixture gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// With a valid bearer token, so that the fallback policy is satisfied and what answers is
    /// routing: nothing is mapped there.
    /// </summary>
    [Theory]
    [InlineData(DashboardFixture.DashboardPath)]
    [InlineData(DashboardFixture.SignOutPath)]
    [InlineData(DashboardFixture.CallbackPath)]
    [InlineData(DashboardFixture.SignedOutCallbackPath)]
    public async Task DashboardPaths_AreNotMapped(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", _gateway.Issuer.CreateToken());

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Without a token the fallback policy answers first, as for any path, so an anonymous caller
    /// cannot even tell whether a dashboard exists.
    /// </summary>
    [Fact]
    public async Task DashboardPath_WithoutAToken_IsUnauthorizedLikeAnyOtherPath()
    {
        using var response = await _gateway.Client.GetAsync(DashboardFixture.DashboardPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheBearerScheme_IsRegistered()
    {
        var schemes = await _gateway.Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();

        Assert.Equal(["Bearer"], schemes.Select(scheme => scheme.Name));
    }
}
