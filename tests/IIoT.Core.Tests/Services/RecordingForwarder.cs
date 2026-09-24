using IIoT.Core.Interfaces;
using IIoT.Core.Models;

namespace IIoT.Core.Tests.Services;

/// <summary>
/// Captures what the service forwarded. Hand-written rather than mocked: the interface has one
/// method, and recording the calls says more plainly what the tests are asserting.
/// </summary>
internal sealed class RecordingForwarder : IDataForwarder
{
    private readonly List<TelemetryMessage> _forwarded = [];

    public IReadOnlyList<TelemetryMessage> Forwarded => _forwarded;

    public TelemetryMessage? Last => _forwarded.Count > 0 ? _forwarded[^1] : null;

    public Task ForwardAsync(TelemetryMessage message, CancellationToken cancellationToken = default)
    {
        _forwarded.Add(message);
        return Task.CompletedTask;
    }
}
