using IIoT.Core.Interfaces;
using IIoT.Core.Models;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// A forwarder that records what it was given and fails when told to, for the tests that are about
/// what the functions decide rather than about the gateway. The tests that need the real forwarder
/// and gateway use <see cref="ForwardingPipeline"/>.
/// </summary>
public sealed class ScriptedForwarder : IDataForwarder
{
    private readonly List<(TelemetryMessage Message, ForwardingContext Context)> _calls = [];

    public IReadOnlyList<(TelemetryMessage Message, ForwardingContext Context)> Calls => _calls;

    /// <summary>When set, every call is recorded and then fails with this.</summary>
    public Exception? FailWith { get; set; }

    public Task ForwardAsync(TelemetryMessage message, ForwardingContext context, CancellationToken cancellationToken = default)
    {
        _calls.Add((message, context));
        return FailWith is null ? Task.CompletedTask : Task.FromException(FailWith);
    }
}
