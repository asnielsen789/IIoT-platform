using IIoT.Functions.DeadLettering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IIoT.Functions;

/// <summary>
/// Registers dead-letter handling (ADR-010): the queues, the retry schedule and the writer the IoT Hub
/// function keeps a failed message with.
/// </summary>
/// <remarks>
/// <para>Kept out of <c>Program.cs</c> so the integration tests compose exactly these registrations.</para>
/// <list type="bullet">
/// <item><see cref="IDeadLetterQueue"/> → <see cref="StorageDeadLetterQueue"/>: a singleton, because it
/// holds the Storage client, which is thread-safe and meant to be reused.</item>
/// <item><see cref="ForwardingRetrySchedule"/>: read once from configuration; settings only change
/// with a restart.</item>
/// <item><see cref="ForwardingDeadLetterWriter"/>: a singleton; everything it holds is one.</item>
/// </list>
/// <para>
/// Like <see cref="ForwardingServiceCollectionExtensions"/>, nothing here reads a connection setting at
/// startup. Without the <c>DeadLetterStorage</c> settings the worker starts, and each dead-letter
/// write fails and is logged at Critical with the setting named.
/// </para>
/// </remarks>
public static class DeadLetterServiceCollectionExtensions
{
    public static IServiceCollection AddForwardingDeadLetters(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IDeadLetterQueue>(_ => new StorageDeadLetterQueue(configuration));
        services.AddSingleton(ForwardingRetrySchedule.FromConfiguration(configuration));
        services.AddSingleton<ForwardingDeadLetterWriter>();

        return services;
    }
}
