using IIoT.Core.Services;
using IIoT.Functions;
using IIoT.Functions.DeadLettering;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IIoT.Integration.Tests.Hosting;

/// <summary>
/// Composes the worker's services the way <c>Program.cs</c> does, with the settings a staging
/// deployment supplies.
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
/// The configuration is staging's, with the settings the app code reads: no <c>GatewayV1</c>
/// resilience section, and the <c>DeadLetterStorage</c> connection either absent, as before a
/// deployment adds it, or present, as the templates set it. The worker must start either way, and
/// both functions must be constructible from what it registered, as the worker activates them per
/// invocation.
/// </para>
/// </remarks>
public class WorkerCompositionTests
{
    public static TheoryData<string> Configurations => new() { "empty", "staging" };

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task Worker_ComposesAndActivatesBothFunctions(string configuration)
    {
        // Asynchronous disposal, as the host does it: one of the Application Insights modules
        // implements only IAsyncDisposable.
        await using var provider = ComposeWorker(Settings(configuration));

        Assert.NotNull(provider.GetRequiredService<TelemetryClient>());
        Assert.IsType<StorageDeadLetterQueue>(provider.GetRequiredService<IDeadLetterQueue>());

        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TelemetryService>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<TelemetryFunction>(scope.ServiceProvider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<RetryForwardingFunction>(scope.ServiceProvider));
    }

    /// <summary>
    /// With staging's settings, the queue client is built for the account's queue endpoint without
    /// touching the network, and the retry schedule is host.json's.
    /// </summary>
    [Fact]
    public async Task StagingSettings_DescribeTheQueueEndpointAndTheSchedule()
    {
        var configuration = Settings("staging");
        await using var provider = ComposeWorker(configuration);

        var client = StorageDeadLetterQueue.CreateServiceClient(configuration);

        Assert.Equal(new Uri("https://stiiotstagingneuxxxx.queue.core.windows.net/"), client.Uri);
        Assert.Equal(
            new ForwardingRetrySchedule(ForwardingRetrySchedule.DefaultMaxAttempts, ForwardingRetrySchedule.DefaultRetryInterval),
            provider.GetRequiredService<ForwardingRetrySchedule>());
    }

    /// <summary>
    /// The worker's default rule drops everything below Warning on its way to Application Insights,
    /// which would discard the platform's information-level rejection reasons.
    /// </summary>
    [Fact]
    public async Task Worker_RemovesTheWarningOnlyApplicationInsightsRule()
    {
        await using var provider = ComposeWorker(Settings("staging"));

        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        Assert.DoesNotContain(rules, rule =>
            rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    }

    /// <summary>
    /// The dead-letter settings as <c>infra/modules/function-app.bicep</c> writes them, with a
    /// placeholder account and identity. The other settings the templates write are read by the host
    /// or by options at first use, not at composition.
    /// </summary>
    private static IConfiguration Settings(string configuration) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(configuration == "staging"
                ? new Dictionary<string, string?>
                {
                    ["DeadLetterStorage:queueServiceUri"] = "https://stiiotstagingneuxxxx.queue.core.windows.net",
                    ["DeadLetterStorage:credential"] = "managedidentity",
                    ["DeadLetterStorage:clientId"] = "00000000-0000-0000-0000-000000000000"
                }
                : [])
            .Build();

    private static ServiceProvider ComposeWorker(IConfiguration configuration)
    {
        // What the Functions host registers before Program.cs adds anything.
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();

        // Program.cs, line for line.
        services.AddTelemetryObservability();
        services.AddTelemetryForwarding(configuration);
        services.AddForwardingDeadLetters(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
