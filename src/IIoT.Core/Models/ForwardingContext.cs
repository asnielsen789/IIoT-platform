namespace IIoT.Core.Models;

/// <summary>
/// What the platform itself established about a message, handed to the forwarder alongside it.
/// </summary>
/// <remarks>
/// The payload says who the device claims to be and when its own clock thinks things happened;
/// the context says what the transport proved. A forwarder takes identity and receipt time from
/// here and never from the payload. The payload's <c>deviceId</c> is optional and informational
/// (section 4 of <c>docs/mqtt-payload.md</c>), so without the context a forwarder could be handed
/// readings that belong to no one.
/// </remarks>
/// <param name="AuthenticatedDeviceId">
/// The identity IoT Hub authenticated the connection as — the system property
/// <c>iothub-connection-device-id</c> — after it has been cross-checked against the payload.
/// Never the payload's <c>deviceId</c>.
/// </param>
/// <param name="EnqueuedAt">
/// When IoT Hub accepted the message. Platform time, stamped by the hub rather than the device,
/// and therefore trustworthy as absolute time in a way that <c>sentAt</c> and <c>measuredAt</c>
/// are not.
/// </param>
public sealed record ForwardingContext(string AuthenticatedDeviceId, DateTimeOffset EnqueuedAt);
