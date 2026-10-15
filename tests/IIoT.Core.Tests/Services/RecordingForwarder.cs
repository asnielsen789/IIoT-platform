using IIoT.Core.Interfaces;
using IIoT.Core.Models;

namespace IIoT.Core.Tests.Services;

/// <summary>
/// Captures what the service forwarded, and the context it forwarded it with. Hand-written
/// rather than mocked: the interface has one method, and recording the calls says more plainly
/// what the tests are asserting.
/// </summary>
internal sealed class RecordingForwarder : IDataForwarder
{
    private readonly List<ForwardedMessage> _forwarded = [];

    public IReadOnlyList<ForwardedMessage> Forwarded => _forwarded;

    public ForwardedMessage? Last => _forwarded.Count > 0 ? _forwarded[^1] : null;

    /// <summary>When set, every call is recorded and then fails with this.</summary>
    public Exception? FailWith { get; set; }

    public Task ForwardAsync(
        TelemetryMessage message,
        ForwardingContext context,
        CancellationToken cancellationToken = default)
    {
        _forwarded.Add(new ForwardedMessage(message, context));
        return FailWith is null ? Task.CompletedTask : Task.FromException(FailWith);
    }
}

/// <summary>One call to <see cref="IDataForwarder.ForwardAsync"/>.</summary>
internal sealed record ForwardedMessage(TelemetryMessage Message, ForwardingContext Context);
