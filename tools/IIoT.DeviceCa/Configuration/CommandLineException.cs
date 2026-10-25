namespace IIoT.DeviceCa.Configuration;

/// <summary>A usage error: the arguments do not form a command.</summary>
/// <remarks>Separate so <c>Program</c> can print usage for this and only the message for the rest.</remarks>
public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message)
    {
    }
}
