using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IIoT.Functions;

/// <summary>
/// Application Insights for the isolated worker, in one place so that a test can run exactly the
/// wiring <c>Program.cs</c> runs.
/// </summary>
/// <remarks>
/// The wiring is tested because it fails at runtime, not at compile time. Application Insights
/// 3.x removed types that <c>Microsoft.Azure.Functions.Worker.ApplicationInsights</c> still uses,
/// so a 3.x package compiles cleanly and then crashes the worker on startup with a
/// <c>TypeLoadException</c>. That is how the first deployment failed.
/// </remarks>
public static class ObservabilityServiceCollectionExtensions
{
    private const string ApplicationInsightsLoggerProvider =
        "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider";

    public static IServiceCollection AddTelemetryObservability(this IServiceCollection services)
    {
        // The connection string comes from APPLICATIONINSIGHTS_CONNECTION_STRING, which the Bicep
        // template supplies; locally its absence simply means nothing is exported.
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // The worker installs a filter that drops anything below Warning before it reaches
        // Application Insights. The security events and rejection reasons this platform logs are
        // exactly what needs to survive, so the default rule is removed. Structured logging and
        // metrics are a separate piece of work.
        services.Configure<LoggerFilterOptions>(options =>
        {
            var defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName == ApplicationInsightsLoggerProvider);

            if (defaultRule is not null)
                options.Rules.Remove(defaultRule);
        });

        return services;
    }
}
