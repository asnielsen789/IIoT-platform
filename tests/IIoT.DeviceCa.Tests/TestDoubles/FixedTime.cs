namespace IIoT.DeviceCa.Tests.TestDoubles;

/// <summary>A clock that always reads the same instant.</summary>
public sealed class FixedTime : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTime(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
