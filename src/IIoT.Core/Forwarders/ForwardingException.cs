using System.Net;

namespace IIoT.Core.Forwarders;

/// <summary>
/// A message could not be delivered downstream: the gateway refused it, or it could not be sent
/// in the first place.
/// </summary>
/// <remarks>The message is written to be logged, and never contains a token or a secret.</remarks>
public sealed class ForwardingException : Exception
{
    public ForwardingException(string message)
        : base(message)
    {
    }

    public ForwardingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The gateway's final answer, when there was one.</summary>
    public HttpStatusCode? StatusCode { get; init; }
}
