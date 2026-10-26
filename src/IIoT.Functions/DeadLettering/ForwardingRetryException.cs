namespace IIoT.Functions.DeadLettering;

/// <summary>
/// A retry of a dead-lettered message failed to forward it, and the queue should try again.
/// </summary>
/// <remarks>
/// Thrown, not returned, because a failed invocation is what tells the queue trigger to keep the
/// entry: it makes it invisible for the retry interval and counts the attempt. The message names the
/// entry and the reason, never its payload, so it is safe in the host's log.
/// </remarks>
public sealed class ForwardingRetryException : Exception
{
    public ForwardingRetryException(string message)
        : base(message)
    {
    }

    public ForwardingRetryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
