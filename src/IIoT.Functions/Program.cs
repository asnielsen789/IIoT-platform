using IIoT.Functions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

// CreateBuilder applies the worker defaults itself — converters, Functions logging, the gRPC
// channel to the host. ConfigureFunctionsWebApplication() would add ASP.NET Core integration for
// HTTP triggers on top; the functions here are the IoT Hub trigger and a queue trigger, neither of
// them HTTP, so it is not called.
var builder = FunctionsApplication.CreateBuilder(args);

// Application Insights, including the logging filter the platform needs. Kept in an extension
// method so a test runs exactly this wiring; see ObservabilityServiceCollectionExtensions.
builder.Services.AddTelemetryObservability();

// The processing pipeline. The forwarder is a typed HTTP client with a resilience handler, and
// the lifetimes are chosen so that nothing holds one beyond a single invocation; the reasoning is
// on ForwardingServiceCollectionExtensions. Settings: GatewayV1 and GatewayAuth.
builder.Services.AddTelemetryForwarding(builder.Configuration);

// Dead-letter handling (ADR-010): the writer ProcessTelemetry keeps a failed message with, and the
// queue RetryForwarding moves an entry to the poison queue with. Settings: DeadLetterStorage, and
// the queue trigger's schedule in host.json. See DeadLetterServiceCollectionExtensions.
builder.Services.AddForwardingDeadLetters(builder.Configuration);

builder.Build().Run();
