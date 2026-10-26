namespace IIoT.Core.DeadLettering;

/// <summary>
/// A message that passed validation and the identity check but could not be forwarded, as it is
/// kept for a later attempt: the entry on the dead-letter queue (ADR-010).
/// </summary>
/// <remarks>
/// <para>
/// It carries everything a retry needs and nothing it would have to trust blindly. The payload is
/// kept exactly as the device sent it, so a retry validates it again, against whatever contract the
/// platform enforces by then; <c>schemaVersion</c> travels inside it, as ADR-004 intends. The
/// identity and the receipt time are what the platform established when IoT Hub delivered the
/// message, which a retry cannot establish again: by then the transport that proved them is gone.
/// </para>
/// <para>
/// Versioned on its own, like both contracts. An entry outlives the build that wrote it, sometimes
/// by days, so a later build must be able to tell an entry it understands from one it does not.
/// </para>
/// </remarks>
public sealed record DeadLetterEnvelope
{
    /// <summary>The only version this build writes and reads. Any other is refused, never guessed at.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// How much of a failure reason an entry keeps. The forwarder's reasons are a status line and at
    /// most 300 characters of the gateway's detail; the cap bounds what an exception from further
    /// down could add, so the fixed part of an entry stays small next to the payload.
    /// </summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Always <see cref="CurrentVersion"/> in an entry this build wrote.</summary>
    public required int EnvelopeVersion { get; init; }

    /// <summary>
    /// The message's <c>messageId</c>, which is also the <c>Idempotency-Key</c> every attempt sends.
    /// Repeated outside the payload so an operator can identify an entry without decoding it.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// The IoT Hub system property <c>iothub-connection-device-id</c> of the original delivery: the
    /// authoritative identity under the trust model, never the payload's <c>deviceId</c>. A retry
    /// cross-checks the payload against it exactly as the first attempt did.
    /// </summary>
    public required string AuthenticatedDeviceId { get; init; }

    /// <summary>
    /// When IoT Hub accepted the message. A retry forwards with it as the receipt time, so a batch
    /// delivered hours later still says when the platform received it.
    /// </summary>
    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>When the entry was written: the end of the first attempt.</summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>Why the first attempt failed.</summary>
    public required ForwardingFailure Failure { get; init; }

    /// <summary>The message body exactly as IoT Hub delivered it.</summary>
    public required string Payload { get; init; }

    /// <summary>A version-<see cref="CurrentVersion"/> entry, with the reason cut to <see cref="MaxReasonLength"/>.</summary>
    public static DeadLetterEnvelope Create(
        string payload,
        string authenticatedDeviceId,
        DateTimeOffset enqueuedAt,
        string messageId,
        string reason,
        int? statusCode,
        DateTimeOffset deadLetteredAt) =>
        new()
        {
            EnvelopeVersion = CurrentVersion,
            MessageId = messageId,
            AuthenticatedDeviceId = authenticatedDeviceId,
            EnqueuedAt = enqueuedAt,
            DeadLetteredAt = deadLetteredAt,
            Failure = new ForwardingFailure(
                reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength] + "...",
                statusCode),
            Payload = payload
        };
}

/// <summary>Why forwarding failed.</summary>
/// <param name="Reason">The forwarder's reason, safe to log: it never contains a token or a secret.</param>
/// <param name="StatusCode">
/// The gateway's final HTTP status, when it answered at all. Optional on the wire: an entry for a
/// failure that never reached the gateway leaves it out.
/// </param>
public sealed record ForwardingFailure(string Reason, int? StatusCode = null);
