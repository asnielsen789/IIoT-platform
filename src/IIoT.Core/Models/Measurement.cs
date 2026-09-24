namespace IIoT.Core.Models;

/// <summary>A single reading. See section 2 of <c>docs/mqtt-payload.md</c>.</summary>
public sealed record Measurement
{
    /// <summary>Only <c>level</c> exists in v1. Extended additively.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// The raw measured value, in <see cref="Unit"/>. No conversion is performed on the
    /// device (ADR-004), and none is performed here.
    /// </summary>
    public required double Value { get; init; }

    /// <summary>UCUM unit code. Only <c>mm</c> in v1.</summary>
    public required string Unit { get; init; }

    /// <summary>
    /// Time of measurement, not of transmission. This may lie far in the past for buffered
    /// backfill, and a message must never be rejected on that basis.
    /// </summary>
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The device's own assessment of the reading. Absent in the payload means <c>ok</c>.</summary>
    public MeasurementQuality Quality { get; init; } = MeasurementQuality.Ok;
}

/// <summary>The device's assessment of a reading's reliability.</summary>
public enum MeasurementQuality
{
    /// <summary>The schema default, and the value assumed when the field is absent.</summary>
    Ok = 0,
    Uncertain = 1,
    Bad = 2
}
