using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace IIoT.GatewayMock.Authentication;

/// <summary>
/// The policy that protects the gateway API: an authenticated caller, authenticated by the bearer
/// scheme and by nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The scheme is named in the policy rather than left to the default scheme. A policy without
/// schemes accepts whatever principal the authentication middleware produced, so it means "bearer"
/// only for as long as bearer is the default. Once the dashboard adds a cookie scheme, a change of
/// default would quietly let a browser's session cookie authenticate <c>POST /v1/readings</c>, and
/// a cookie is sent by the browser on its own: any page the user visits could then deliver
/// readings in their name (cross-site request forgery). Named here, the bearer scheme is the only
/// one ever asked, whatever else is registered.
/// </para>
/// <para>
/// It serves as the endpoint's policy, the default policy (an endpoint that asks for "authorization"
/// without naming a policy) and the fallback policy (an endpoint that asks for nothing), so an
/// endpoint added later is bearer-only unless it deliberately says otherwise, as the dashboard does.
/// </para>
/// </remarks>
internal static class ApiAuthorization
{
    public static AuthorizationPolicy Policy { get; } =
        new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
}
