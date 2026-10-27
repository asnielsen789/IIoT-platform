using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace IIoT.Functions.DeadLettering;

/// <summary>
/// How often, and how far apart, a dead-lettered message is retried: the queue trigger's
/// <c>maxDequeueCount</c> and <c>visibilityTimeout</c>, as the code needs to know them too.
/// </summary>
/// <remarks>
/// <para>
/// The runtime owns the schedule. On a failed attempt it makes the entry invisible for
/// <c>visibilityTimeout</c> ("the time interval between retries when processing of a message
/// fails") and, once an attempt fails with the dequeue count at <c>maxDequeueCount</c> ("the number
/// of times to try processing a message before moving it to the poison queue"), moves it to the
/// poison queue. The values live in <c>host.json</c>, <c>extensions.queues</c>: 8 attempts, 30
/// minutes apart.
/// </para>
/// <para>
/// The code needs them for two decisions the runtime cannot make well. The first entry is written
/// invisible for one interval, so the first retry does not follow straight on a failure the
/// forwarder has just spent its own retries on. And the last attempt moves the entry to the poison
/// queue itself, because the runtime's move writes it with the service's default time-to-live of
/// seven days, after which an entry an operator had not reached yet would be gone.
/// </para>
/// <para>
/// The defaults below must equal <c>host.json</c>; a test reads the file and fails if they drift.
/// An app setting that overrides <c>host.json</c>, such as
/// <c>AzureFunctionsJobHost__extensions__queues__visibilityTimeout</c>, reaches the worker's
/// configuration as well, and is honoured here the same way.
/// </para>
/// </remarks>
/// <param name="MaxAttempts">Attempts before an entry goes to the poison queue.</param>
/// <param name="RetryInterval">The pause after a failed attempt.</param>
public sealed record ForwardingRetrySchedule(int MaxAttempts, TimeSpan RetryInterval)
{
    public const int DefaultMaxAttempts = 8;

    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(30);

    private const string QueuesSection = "AzureFunctionsJobHost:extensions:queues";

    /// <summary>The schedule <c>host.json</c> sets, with any app-setting override applied.</summary>
    public static ForwardingRetrySchedule FromConfiguration(IConfiguration configuration)
    {
        var queues = configuration.GetSection(QueuesSection);

        var maxAttempts = int.TryParse(queues["maxDequeueCount"], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : DefaultMaxAttempts;

        var retryInterval = TimeSpan.TryParse(queues["visibilityTimeout"], CultureInfo.InvariantCulture, out var interval) && interval >= TimeSpan.Zero
            ? interval
            : DefaultRetryInterval;

        return new ForwardingRetrySchedule(maxAttempts, retryInterval);
    }
}
