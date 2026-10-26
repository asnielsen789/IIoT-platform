using IIoT.GatewayMock.Authentication;
using IIoT.GatewayMock.Dashboard;
using IIoT.GatewayMock.Readings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

// A stand-in for the downstream gateway: it accepts the platform's readings contract
// (docs/gateway-contract.md) and nothing else. A development tool that never ships.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GatewayJwtOptions>()
    .BindConfiguration(GatewayJwtOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GatewayJwtOptions>, GatewayJwtOptionsValidator>();

var authentication = builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Registered after AddJwtBearer on purpose: configure actions run in registration order, so
// these rules win over anything the framework binds from Authentication:Schemes:Bearer.
builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();

// The dashboard is opt-in. Without GatewayMock:Dashboard:Authority none of it is registered or
// mapped — no cookie, no sign-in, no page — and the mock is exactly the API it is without one.
var dashboardEnabled = DashboardOptions.IsEnabled(builder.Configuration);
if (dashboardEnabled)
{
    builder.Services.AddDashboard(authentication);
}

// No anonymous endpoints. The fallback policy applies to every endpoint that does not state its
// own, so an endpoint added later is protected without anyone remembering to protect it. Default
// and fallback both name the bearer scheme (see ApiAuthorization): no other scheme can satisfy them.
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(ApiAuthorization.Policy)
    .SetFallbackPolicy(ApiAuthorization.Policy);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ReadingStore>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapReadings();

if (dashboardEnabled)
{
    app.MapDashboard();
    app.Logger.LogInformation("Dashboard enabled at {Route}", DashboardEndpoints.Route);
}
else
{
    app.Logger.LogInformation(
        "Dashboard disabled: {Setting} is not set", $"{DashboardOptions.SectionName}:{nameof(DashboardOptions.Authority)}");
}

app.Run();
