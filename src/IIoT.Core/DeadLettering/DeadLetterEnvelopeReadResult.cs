namespace IIoT.Core.DeadLettering;

/// <summary>The outcome of reading a dead-letter entry back from its queue.</summary>
public sealed record DeadLetterEnvelopeReadResult
{
    private DeadLetterEnvelopeReadResult(DeadLetterEnvelope? envelope, string? error)
    {
        Envelope = envelope;
        Error = error;
    }

    /// <summary>The envelope, present only when <see cref="IsValid"/> is true.</summary>
    public DeadLetterEnvelope? Envelope { get; }

    /// <summary>Why the entry was refused. Safe to log: it names fields, not their contents.</summary>
    public string? Error { get; }

    public bool IsValid => Envelope is not null;

    public static DeadLetterEnvelopeReadResult Valid(DeadLetterEnvelope envelope) => new(envelope, null);

    public static DeadLetterEnvelopeReadResult Invalid(string error) => new(null, error);
}
