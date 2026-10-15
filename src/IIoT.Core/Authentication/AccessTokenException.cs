namespace IIoT.Core.Authentication;

/// <summary>
/// No access token could be obtained: the token endpoint refused, failed or answered with
/// something that is not a token, or the client is not configured.
/// </summary>
/// <remarks>
/// The message is written to be logged. It never contains the client secret or a token.
/// </remarks>
public sealed class AccessTokenException : Exception
{
    public AccessTokenException(string message)
        : base(message)
    {
    }

    public AccessTokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
