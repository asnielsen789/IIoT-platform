namespace IIoT.Functions.DeadLettering;

/// <summary>
/// Writes entries to the two dead-letter queues. Only writes: reading <c>forwarding-failed</c> is the
/// queue trigger's job, and nothing in the app reads the poison queue.
/// </summary>
/// <remarks>
/// The entries are text the caller has already serialised and size-checked. Kept that narrow so a
/// test can stand in for the queue with a list, and so the queue never decides anything about a
/// message.
/// </remarks>
public interface IDeadLetterQueue
{
    /// <summary>
    /// Adds <paramref name="entry"/> to <see cref="DeadLetterQueues.ForwardingFailed"/>, invisible
    /// for <paramref name="initialDelay"/>, and without expiry.
    /// </summary>
    Task AddAsync(string entry, TimeSpan initialDelay, CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="entry"/> to <see cref="DeadLetterQueues.ForwardingFailedPoison"/>, without expiry.</summary>
    Task AddToPoisonAsync(string entry, CancellationToken cancellationToken = default);
}
