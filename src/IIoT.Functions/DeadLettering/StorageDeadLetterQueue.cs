using Azure.Core;
using Azure.Identity;
using Azure.Storage.Queues;
using IIoT.Core.Security;
using Microsoft.Extensions.Configuration;

namespace IIoT.Functions.DeadLettering;

/// <summary>
/// The dead-letter queues in Azure Storage, reached with the same connection settings as the
/// <c>RetryForwarding</c> trigger, so that the writer and the reader always mean the same queues
/// and the same identity.
/// </summary>
/// <remarks>
/// <para>The settings, read from the <see cref="DeadLetterQueues.ConnectionName"/> section:</para>
/// <list type="bullet">
/// <item><c>queueServiceUri</c> (or its alias <c>serviceUri</c>), <c>credential</c> and
/// <c>clientId</c>: an identity-based connection, as Microsoft documents it for the queue trigger.
/// In Azure, <c>credential</c> is <c>managedidentity</c> and <c>clientId</c> the Function's
/// user-assigned identity, so no key exists anywhere.</item>
/// <item>The section's own value instead, as a connection string. Only for local development
/// against Azurite (<c>UseDevelopmentStorage=true</c>); the storage account in Azure has shared-key
/// access disabled.</item>
/// </list>
/// <para>
/// Nothing is read or checked at startup, for the reason the gateway settings are not: a missing
/// setting must not stop the worker from starting, validating and logging device traffic. The
/// client is built on first use; a setting that is missing or insecure then fails that write, with
/// the setting named, and the caller logs it.
/// </para>
/// <para>
/// Entries are base64-encoded, the queue trigger's default <c>messageEncoding</c>, and never expire.
/// The service's default time-to-live is seven days (Put Message: "If this parameter is omitted, the
/// default time-to-live is 7 days"), after which an entry nobody had looked at would vanish without a
/// trace; <c>-1</c> means it does not expire. Transient failures are retried by the Storage client's
/// own retry policy.
/// </para>
/// </remarks>
public sealed class StorageDeadLetterQueue : IDeadLetterQueue
{
    /// <summary>The service's value for "does not expire".</summary>
    internal static readonly TimeSpan NeverExpires = TimeSpan.FromSeconds(-1);

    private const string ManagedIdentityCredential = "managedidentity";

    private readonly Lazy<QueueServiceClient> _service;

    public StorageDeadLetterQueue(IConfiguration configuration)
    {
        // Built once and shared: the client is thread-safe and keeps its connections. A
        // configuration fault is cached with it, which is fine, since settings only change with a
        // restart.
        _service = new Lazy<QueueServiceClient>(() => CreateServiceClient(configuration));
    }

    public async Task AddAsync(string entry, TimeSpan initialDelay, CancellationToken cancellationToken = default) =>
        await Queue(DeadLetterQueues.ForwardingFailed)
            .SendMessageAsync(entry, visibilityTimeout: initialDelay, timeToLive: NeverExpires, cancellationToken)
            .ConfigureAwait(false);

    public async Task AddToPoisonAsync(string entry, CancellationToken cancellationToken = default) =>
        await Queue(DeadLetterQueues.ForwardingFailedPoison)
            .SendMessageAsync(entry, visibilityTimeout: null, timeToLive: NeverExpires, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The queue service client the settings describe. Public so that a test can check the reading
    /// of the settings without a network.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings are missing, contradictory or insecure.</exception>
    public static QueueServiceClient CreateServiceClient(IConfiguration configuration)
    {
        var section = configuration.GetSection(DeadLetterQueues.ConnectionName);
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };

        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            return new QueueServiceClient(section.Value, options);
        }

        var setting = $"{DeadLetterQueues.ConnectionName}__queueServiceUri";
        var uriText = section["queueServiceUri"] ?? section["serviceUri"];
        if (string.IsNullOrWhiteSpace(uriText))
        {
            throw new InvalidOperationException(
                $"The dead-letter queues are not configured: set {setting}, {DeadLetterQueues.ConnectionName}__credential " +
                $"and {DeadLetterQueues.ConnectionName}__clientId");
        }

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{setting} must be an absolute URL");
        }

        // The platform's one rule for outbound HTTP. The entry carries the device's readings, and
        // the request a bearer token.
        var violation = TransportSecurity.DescribeViolation(uri, setting);
        if (violation is not null)
        {
            throw new InvalidOperationException(violation);
        }

        return new QueueServiceClient(uri, CreateCredential(section), options);
    }

    /// <summary>
    /// The credential the trigger uses for the same settings: the managed identity when
    /// <c>credential</c> is <c>managedidentity</c>, the user-assigned one that <c>clientId</c> (or
    /// <c>managedIdentityResourceId</c>) names. Without <c>credential</c>, which Microsoft documents
    /// for local development only, the developer's own sign-in; the templates always set it.
    /// </summary>
    private static TokenCredential CreateCredential(IConfigurationSection section)
    {
        if (!string.Equals(section["credential"], ManagedIdentityCredential, StringComparison.OrdinalIgnoreCase))
        {
            return new DefaultAzureCredential();
        }

        var clientId = section["clientId"];
        var resourceId = section["managedIdentityResourceId"];

        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(resourceId))
        {
            throw new InvalidOperationException(
                $"{DeadLetterQueues.ConnectionName}__clientId and __managedIdentityResourceId are mutually exclusive");
        }

        var identity = !string.IsNullOrWhiteSpace(clientId)
            ? ManagedIdentityId.FromUserAssignedClientId(clientId)
            : !string.IsNullOrWhiteSpace(resourceId)
                ? ManagedIdentityId.FromUserAssignedResourceId(new ResourceIdentifier(resourceId))
                : ManagedIdentityId.SystemAssigned;

        return new ManagedIdentityCredential(identity);
    }

    private QueueClient Queue(string name) => _service.Value.GetQueueClient(name);
}
