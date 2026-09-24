using IIoT.Core.Models;

namespace IIoT.Core.Validation;

/// <summary>The outcome of validating a raw payload against the v1 contract.</summary>
public sealed record TelemetryValidationResult
{
    private TelemetryValidationResult(TelemetryMessage? message, string? error)
    {
        Message = message;
        Error = error;
    }

    /// <summary>The deserialised message, present only when <see cref="IsValid"/> is true.</summary>
    public TelemetryMessage? Message { get; }

    /// <summary>Why the payload was rejected. Safe to log: it describes the payload, not its contents.</summary>
    public string? Error { get; }

    public bool IsValid => Message is not null;

    public static TelemetryValidationResult Valid(TelemetryMessage message) => new(message, null);

    public static TelemetryValidationResult Invalid(string error) => new(null, error);
}
