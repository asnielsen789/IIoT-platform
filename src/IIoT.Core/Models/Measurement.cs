namespace IIoT.Core.Models;

/// <summary>A single reading. See section 2 of <c>docs/mqtt-payload.md</c>.</summary>
public sealed record Measurement
{
    /// <summary>
    /// <c>level</c>, <c>energy</c>, <c>power</c>, <c>volume</c> or <c>count</c> (ADR-011). Extended
    /// additively; the schema ties each type to its unit and value range.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// The raw measured value, in <see cref="Unit"/>. No conversion is performed on the
    /// device (ADR-004), and none is performed here.
    /// </summary>
    public required double Value { get; init; }

    /// <summary>UCUM unit code, fixed by <see cref="Type"/>: <c>mm</c>, <c>kW.h</c>, <c>kW</c>, <c>L</c> or <c>1</c>.</summary>
    public required string Unit { get; init; }

    /// <summary>
    /// Which of the device's sensors or meters took the measurement, where it has more than one of
    /// a type, such as <c>oil</c> and <c>sludge</c> on an oil separator; <c>null</c> otherwise. A
    /// device's series is its type and sensor together (ADR-011).
    /// </summary>
    public string? Sensor { get; init; }

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
