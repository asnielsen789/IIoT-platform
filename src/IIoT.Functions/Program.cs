using IIoT.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights, including the logging filter the platform needs. Kept in an extension
// method so a test runs exactly this wiring; see ObservabilityServiceCollectionExtensions.
builder.Services.AddTelemetryObservability();

// The processing pipeline. The forwarder is a typed HTTP client with a resilience handler, and
// the lifetimes are chosen so that nothing holds one beyond a single invocation; the reasoning is
// on ForwardingServiceCollectionExtensions. Settings: GatewayV1 and GatewayAuth.
builder.Services.AddTelemetryForwarding(builder.Configuration);

builder.Build().Run();
