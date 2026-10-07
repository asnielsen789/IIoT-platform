namespace IIoT.TestClient.Configuration;

/// <summary>A usage error: the arguments or environment cannot produce a runnable configuration.</summary>
/// <remarks>
/// Separate from the other exception types so <c>Program</c> can print usage for this and a stack
/// trace for everything else.
/// </remarks>
public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message)
    {
    }

    public CommandLineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
