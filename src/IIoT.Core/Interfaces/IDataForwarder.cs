using IIoT.Core.Models;

namespace IIoT.Core.Interfaces;

/// <summary>
/// Sends a validated telemetry message on to a downstream system.
/// </summary>
/// <remarks>
/// The whole message is passed rather than one reading at a time, per ADR-004. A device that
/// has buffered readings through a network outage sends them in one message, and splitting
/// that into separate calls would discard the fact that they arrived together — which is
/// exactly what a downstream system needs in order to tell backfill from live data.
/// </remarks>
public interface IDataForwarder
{
    Task ForwardAsync(TelemetryMessage message, CancellationToken cancellationToken = default);
}
