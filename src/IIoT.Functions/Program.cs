using IIoT.Core.Forwarders;
using IIoT.Core.Interfaces;
using IIoT.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights. Connection string comes from APPLICATIONINSIGHTS_CONNECTION_STRING,
// which the Bicep template supplies; locally its absence simply means nothing is exported.
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

// The worker installs a filter that drops anything below Warning before it reaches Application
// Insights. The security events and rejection reasons this platform logs are exactly what needs
// to survive, so the default rule is removed. Structured logging and metrics are a separate
// piece of work.
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");

    if (defaultRule is not null)
        options.Rules.Remove(defaultRule);
});

builder.Services.AddHttpClient<GatewayV1Forwarder>();
builder.Services.AddSingleton<IDataForwarder, GatewayV1Forwarder>();
builder.Services.AddSingleton<TelemetryService>();

builder.Build().Run();
