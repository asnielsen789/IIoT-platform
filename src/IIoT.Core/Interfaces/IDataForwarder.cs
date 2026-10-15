using IIoT.Core.Models;

namespace IIoT.Core.Interfaces;

/// <summary>
/// Sends a validated telemetry message on to a downstream system.
/// </summary>
/// <remarks>
/// <para>
/// The whole message is passed rather than one reading at a time, per ADR-004. A device that
/// has buffered readings through a network outage sends them in one message, and splitting
/// that into separate calls would discard the fact that they arrived together — which is
/// exactly what a downstream system needs in order to tell backfill from live data.
/// </para>
/// <para>
/// The <see cref="ForwardingContext"/> travels with it because the message alone cannot say whose
/// readings these are: its <c>deviceId</c> is optional and untrusted. An implementation takes the
/// device identity and the receipt time from the context, never from the payload.
/// </para>
/// </remarks>
public interface IDataForwarder
{
    /// <summary>
    /// Delivers <paramref name="message"/> downstream. A failure to deliver is reported by
    /// throwing; the caller decides what that means for the message.
    /// </summary>
    Task ForwardAsync(TelemetryMessage message, ForwardingContext context, CancellationToken cancellationToken = default);
}
