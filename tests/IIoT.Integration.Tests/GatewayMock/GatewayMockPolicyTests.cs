using IIoT.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// Every policy the API can be reached through names the bearer scheme, and only that scheme.
/// </summary>
/// <remarks>
/// A policy without schemes accepts whatever the default scheme produced, so it is bearer-only
/// only by accident of configuration. These tests read the policies the running mock actually
/// evaluates. The behaviour they protect — a dashboard session cookie is refused by the API — is
/// tested end to end with the dashboard enabled.
/// </remarks>
public class GatewayMockPolicyTests : IClassFixture<GatewayMockFixture>
{
    /// <summary>The bearer scheme's name, spelled out rather than borrowed from the framework.</summary>
    private static readonly string[] BearerOnly = ["Bearer"];

    private readonly GatewayMockFixture _gateway;

    public GatewayMockPolicyTests(GatewayMockFixture gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// The fallback covers endpoints that ask for nothing; the default covers endpoints that ask
    /// for authorization without naming a policy. Both are where a later endpoint would land.
    /// </summary>
    [Fact]
    public async Task DefaultAndFallbackPolicies_NameOnlyTheBearerScheme()
    {
        var policies = _gateway.Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await policies.GetFallbackPolicyAsync();
        var defaultPolicy = await policies.GetDefaultPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Equal(BearerOnly, fallback!.AuthenticationSchemes);
        Assert.Equal(BearerOnly, defaultPolicy.AuthenticationSchemes);
    }

    /// <summary>
    /// The policy the authorization middleware evaluates for <c>POST /v1/readings</c>, combined
    /// from the endpoint's metadata exactly as the middleware combines it.
    /// </summary>
    [Fact]
    public async Task ReadingsEndpoint_NamesOnlyTheBearerScheme()
    {
        var services = _gateway.Factory.Services;
        var endpoint = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == GatewayRequests.Route);

        var effective = await AuthorizationPolicy.CombineAsync(
            services.GetRequiredService<IAuthorizationPolicyProvider>(),
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

        Assert.NotNull(effective);
        Assert.Equal(BearerOnly, effective!.AuthenticationSchemes);
    }
}
