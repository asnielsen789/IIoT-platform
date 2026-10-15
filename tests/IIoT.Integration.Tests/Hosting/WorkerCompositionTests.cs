using IIoT.Core.Services;
using IIoT.Functions;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IIoT.Integration.Tests.Hosting;

/// <summary>
/// Composes the worker's services the way <c>Program.cs</c> does, with the settings a staging
/// deployment supplies today.
/// </summary>
/// <remarks>
/// <para>
/// This guards failures that compile cleanly and only surface when the worker starts — where they
/// crash it before any function is indexed, and the host reports nothing more specific than a
/// language worker that keeps restarting. The first deployment hit two in a row:
/// </para>
/// <list type="bullet">
/// <item>Application Insights 3.x removed types that the Functions worker's integration still
/// uses, so registering it threw a <c>TypeLoadException</c>.</item>
/// <item>The gateway's resilience section is optional, but binding an absent section threw. Every
/// other test configures it.</item>
/// </list>
/// <para>
/// The configuration is deliberately empty: staging sets no <c>GatewayV1</c> or
/// <c>GatewayAuth</c> settings until a gateway exists, and the worker must start without them.
/// </para>
/// </remarks>
public class WorkerCompositionTests
{
    [Fact]
    public async Task Worker_ComposesWithoutGatewaySettings()
    {
        // Asynchronous disposal, as the host does it: one of the Application Insights modules
        // implements only IAsyncDisposable.
        await using var provider = ComposeWorker();

        Assert.NotNull(provider.GetRequiredService<TelemetryClient>());

        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TelemetryService>());
    }

    /// <summary>
    /// The worker's default rule drops everything below Warning on its way to Application Insights,
    /// which would discard the platform's information-level rejection reasons.
    /// </summary>
    [Fact]
    public async Task Worker_RemovesTheWarningOnlyApplicationInsightsRule()
    {
        await using var provider = ComposeWorker();

        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        Assert.DoesNotContain(rules, rule =>
            rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    }

    private static ServiceProvider ComposeWorker()
    {
        var configuration = new ConfigurationBuilder().Build();

        // What the Functions host registers before Program.cs adds anything.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        services.AddTelemetryObservability();
        services.AddTelemetryForwarding(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
