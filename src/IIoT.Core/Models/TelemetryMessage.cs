namespace IIoT.Core.Models;

/// <summary>
/// A telemetry message as defined by <c>docs/schemas/telemetry-v1.schema.json</c>.
/// </summary>
/// <remarks>
/// Deserialisation is only ever performed on a payload that has already passed schema
/// validation, so the required members are guaranteed to be present. Unknown fields are
/// ignored rather than rejected, per the evolution rules in section 6 of the contract.
/// </remarks>
public sealed record TelemetryMessage
{
    /// <summary>Always 1 in this version. Any other value is rejected, never interpreted as v1.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Unique per message. Used for idempotency when a device resends.</summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Informational only, and optional. The authoritative identity is the IoT Hub system
    /// property <c>iothub-connection-device-id</c> — see section 4 of the contract. This field
    /// exists to be cross-checked against it, not to be trusted.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>Transmission time according to the device clock. Not trustworthy as absolute time.</summary>
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Monotonically increasing per device. Gaps indicate lost messages.</summary>
    public int? Sequence { get; init; }

    /// <summary>
    /// One or more measurements. Several entries mean the device is transmitting readings it
    /// buffered during a network outage.
    /// </summary>
    public required IReadOnlyList<Measurement> Measurements { get; init; }

    public required DeviceMetadata Device { get; init; }
}
