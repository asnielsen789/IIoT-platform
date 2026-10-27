namespace IIoT.Core.Services;

/// <summary>
/// What <see cref="TelemetryService"/> decided about a message, and what the caller needs in order
/// to act on that decision.
/// </summary>
/// <remarks>
/// The outcome alone was enough while a forwarding failure ended in a log line. A caller that keeps
/// the message after a failure (ADR-010) also needs the message's id, which only the validated
/// message has, and why forwarding failed, which only the exception said. Both are carried here so
/// that the caller never has to parse the payload a second time, or read a log, to find them.
/// </remarks>
/// <param name="Outcome">The decision.</param>
/// <param name="MessageId">
/// The validated message's <c>messageId</c>. Absent when the payload was rejected before it could
/// be read, as an invalid payload is.
/// </param>
/// <param name="FailureReason">
/// Why forwarding failed, written to be logged and stored: the forwarder's own message, which never
/// contains a token or a secret. Present only for <see cref="ProcessingOutcome.ForwardingFailed"/>.
/// </param>
/// <param name="StatusCode">
/// The gateway's final HTTP status, when it answered at all. Absent for a failure that never
/// reached it, such as a network error or a token that could not be obtained.
/// </param>
public sealed record ProcessingResult(
    ProcessingOutcome Outcome,
    string? MessageId = null,
    string? FailureReason = null,
    int? StatusCode = null)
{
    public static ProcessingResult Forwarded(string messageId) => new(ProcessingOutcome.Forwarded, messageId);

    public static ProcessingResult Rejected(ProcessingOutcome outcome, string? messageId = null) => new(outcome, messageId);

    public static ProcessingResult ForwardingFailed(string messageId, string reason, int? statusCode) =>
        new(ProcessingOutcome.ForwardingFailed, messageId, reason, statusCode);
}
