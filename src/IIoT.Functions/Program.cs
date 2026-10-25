using IIoT.Functions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

// CreateBuilder applies the worker defaults itself — converters, Functions logging, the gRPC
// channel to the host. ConfigureFunctionsWebApplication() would add ASP.NET Core integration for
// HTTP triggers on top; the only function here is the IoT Hub trigger, so it is not called.
var builder = FunctionsApplication.CreateBuilder(args);

// Application Insights, including the logging filter the platform needs. Kept in an extension
// method so a test runs exactly this wiring; see ObservabilityServiceCollectionExtensions.
builder.Services.AddTelemetryObservability();

// The processing pipeline. The forwarder is a typed HTTP client with a resilience handler, and
// the lifetimes are chosen so that nothing holds one beyond a single invocation; the reasoning is
// on ForwardingServiceCollectionExtensions. Settings: GatewayV1 and GatewayAuth.
builder.Services.AddTelemetryForwarding(builder.Configuration);

builder.Build().Run();
