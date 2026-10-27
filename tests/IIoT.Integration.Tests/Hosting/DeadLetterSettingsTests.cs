using System.Text.Json;
using IIoT.Functions.DeadLettering;
using Microsoft.Extensions.Configuration;

namespace IIoT.Integration.Tests.Hosting;

/// <summary>
/// The settings dead-letter handling depends on, read the way the worker reads them: the retry
/// schedule in <c>host.json</c>, and the <c>DeadLetterStorage</c> connection.
/// </summary>
public class DeadLetterSettingsTests
{
    /// <summary>
    /// The runtime spaces and counts the attempts from host.json; the code needs the same numbers for
    /// the first entry's delay and the last attempt's move to the poison queue. If they drift apart,
    /// the code moves an entry to the poison queue too early, or leaves the last attempt to the
    /// runtime, whose move lets the entry expire after seven days.
    /// </summary>
    [Fact]
    public void HostJson_Schedule_EqualsTheCodesDefaults()
    {
        using var hostJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Functions", "host.json")));
        var queues = hostJson.RootElement.GetProperty("extensions").GetProperty("queues");

        Assert.Equal(ForwardingRetrySchedule.DefaultMaxAttempts, queues.GetProperty("maxDequeueCount").GetInt32());
        Assert.Equal(ForwardingRetrySchedule.DefaultRetryInterval, TimeSpan.Parse(queues.GetProperty("visibilityTimeout").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        // The trigger's default message encoding, which StorageDeadLetterQueue writes in.
        Assert.False(queues.TryGetProperty("messageEncoding", out _), "the dead-letter queue client writes base64, the trigger's default");
    }

    /// <summary>
    /// An app setting overriding host.json, as an operator might set for a live test of the poison
    /// path, reaches the worker's configuration and the code follows it.
    /// </summary>
    [Fact]
    public void AppSettingOverride_IsHonoured()
    {
        var schedule = ForwardingRetrySchedule.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureFunctionsJobHost:extensions:queues:maxDequeueCount"] = "2",
                ["AzureFunctionsJobHost:extensions:queues:visibilityTimeout"] = "00:00:30"
            })
            .Build());

        Assert.Equal(new ForwardingRetrySchedule(2, TimeSpan.FromSeconds(30)), schedule);
    }

    [Fact]
    public void ConnectionString_IsAcceptedForLocalDevelopment()
    {
        var client = StorageDeadLetterQueue.CreateServiceClient(Configuration(new() { ["DeadLetterStorage"] = "UseDevelopmentStorage=true" }));

        Assert.Equal("devstoreaccount1", client.AccountName);
    }

    [Fact]
    public void ServiceUriAlias_IsAccepted()
    {
        var client = StorageDeadLetterQueue.CreateServiceClient(Configuration(new()
        {
            ["DeadLetterStorage:serviceUri"] = "https://stiiotstagingneuxxxx.queue.core.windows.net",
            ["DeadLetterStorage:credential"] = "managedidentity"
        }));

        Assert.Equal("stiiotstagingneuxxxx", client.AccountName);
    }

    public static TheoryData<Dictionary<string, string?>, string> RefusedSettings => new()
    {
        { new(), "set DeadLetterStorage__queueServiceUri" },
        { new() { ["DeadLetterStorage:queueServiceUri"] = "stiiotstagingneuxxxx" }, "must be an absolute URL" },
        { new() { ["DeadLetterStorage:queueServiceUri"] = "http://stiiotstagingneuxxxx.queue.core.windows.net" }, "must use https" },
        {
            new()
            {
                ["DeadLetterStorage:queueServiceUri"] = "https://stiiotstagingneuxxxx.queue.core.windows.net",
                ["DeadLetterStorage:credential"] = "managedidentity",
                ["DeadLetterStorage:clientId"] = "00000000-0000-0000-0000-000000000000",
                ["DeadLetterStorage:managedIdentityResourceId"] = "/subscriptions/x/resourceGroups/y/providers/Microsoft.ManagedIdentity/userAssignedIdentities/z"
            },
            "mutually exclusive"
        }
    };

    /// <summary>
    /// A missing, malformed or insecure setting is refused with the setting named. The refusal comes
    /// at the first write, which logs it, never at startup.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedSettings))]
    public void UnusableSettings_AreRefused_NamingTheProblem(Dictionary<string, string?> settings, string problem)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => StorageDeadLetterQueue.CreateServiceClient(Configuration(settings)));

        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
