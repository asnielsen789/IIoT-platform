using System.Collections.Concurrent;
using IIoT.Functions.DeadLettering;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// The two dead-letter queues as lists. Hand-written rather than mocked: the interface has two
/// methods, and keeping what was written says plainly what the tests assert.
/// </summary>
public sealed class RecordingDeadLetterQueue : IDeadLetterQueue
{
    /// <summary>Entries written to <c>forwarding-failed</c>, with the delay each was written with.</summary>
    public ConcurrentQueue<(string Entry, TimeSpan InitialDelay)> Added { get; } = new();

    /// <summary>Entries written to <c>forwarding-failed-poison</c>.</summary>
    public ConcurrentQueue<string> Poisoned { get; } = new();

    /// <summary>When set, every write to <c>forwarding-failed</c> fails with this.</summary>
    public Exception? AddFailsWith { get; set; }

    /// <summary>When set, every write to <c>forwarding-failed-poison</c> fails with this.</summary>
    public Exception? PoisonFailsWith { get; set; }

    public Task AddAsync(string entry, TimeSpan initialDelay, CancellationToken cancellationToken = default)
    {
        if (AddFailsWith is not null)
            return Task.FromException(AddFailsWith);

        Added.Enqueue((entry, initialDelay));
        return Task.CompletedTask;
    }

    public Task AddToPoisonAsync(string entry, CancellationToken cancellationToken = default)
    {
        if (PoisonFailsWith is not null)
            return Task.FromException(PoisonFailsWith);

        Poisoned.Enqueue(entry);
        return Task.CompletedTask;
    }
}
