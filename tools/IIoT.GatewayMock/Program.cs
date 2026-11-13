using IIoT.GatewayMock.Authentication;
using IIoT.GatewayMock.Dashboard;
using IIoT.GatewayMock.Hosting;
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

// Behind a TLS-terminating proxy the sign-in needs the scheme the browser used, from the proxy's
// X-Forwarded-Proto — believed only from addresses the configuration trusts. On with the dashboard
// unless GatewayMock:ForwardedHeaders:Enabled says otherwise; see GatewayForwardedHeadersOptions.
var forwardedHeadersEnabled = builder.Services.AddGatewayForwardedHeaders(builder.Configuration, dashboardEnabled);

// No anonymous endpoints. The fallback policy applies to every endpoint that does not state its
// own, so an endpoint added later is protected without anyone remembering to protect it. Default
// and fallback both name the bearer scheme (see ApiAuthorization): no other scheme can satisfy them.
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(ApiAuthorization.Policy)
    .SetFallbackPolicy(ApiAuthorization.Policy);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ReadingStore>();
builder.Services.AddSingleton(TimeProvider.System);

// The archive is opt-in too (ADR-012). Without GatewayMock:Archive:ContainerUri the mock holds
// what it accepts in memory only, and a restart empties it.
var archiveEnabled = ReadingArchiveOptions.IsEnabled(builder.Configuration);
if (archiveEnabled)
{
    builder.Services.AddOptions<ReadingArchiveOptions>()
        .BindConfiguration(ReadingArchiveOptions.SectionName)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<ReadingArchiveOptions>, ReadingArchiveOptionsValidator>();
    builder.Services.AddSingleton<IReadingArchive, BlobReadingArchive>();
}

// Takes whichever archive is registered: none, the blob container, or a test's stand-in.
builder.Services.AddSingleton(services => new ReadingHistory(
    services.GetRequiredService<ReadingStore>(),
    services.GetService<IReadingArchive>(),
    services.GetRequiredService<ILogger<ReadingHistory>>()));

var app = builder.Build();

// First, so that everything after it, the sign-in included, sees the scheme the browser used.
if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

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

if (archiveEnabled)
{
    app.Logger.LogInformation(
        "Archive enabled: accepted batches are kept in {Container}",
        app.Services.GetRequiredService<IOptions<ReadingArchiveOptions>>().Value.ContainerUri);
}
else
{
    app.Logger.LogInformation(
        "Archive disabled: {Setting} is not set; accepted batches are held in memory only",
        $"{ReadingArchiveOptions.SectionName}:{nameof(ReadingArchiveOptions.ContainerUri)}");
}

app.Run();
