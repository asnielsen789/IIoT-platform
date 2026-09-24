namespace IIoT.Core.Models;

/// <summary>
/// Per-message device metadata. Deliberately small: CRA Annex I Part I point (g) limits this
/// to what is necessary for the purpose, so static attributes (model, hardware revision,
/// ICCID) belong in the IoT Hub device twin instead. See section 5 of the contract.
/// </summary>
public sealed record DeviceMetadata
{
    /// <summary>
    /// The running firmware version — the only required metadata field. It is the key that
    /// links a device in the field to the vendor's SBOM, identifies which devices run a
    /// vulnerable version, and verifies that an update actually landed (CRA Annex I Part II,
    /// points 1, 2 and 7).
    /// </summary>
    public required string Firmware { get; init; }

    /// <summary>Operationally necessary for a large battery-powered fleet. Optional.</summary>
    public BatteryStatus? Battery { get; init; }

    /// <summary>NB-IoT radio diagnostics, when the modem exposes them. Optional.</summary>
    public SignalQuality? Signal { get; init; }
}

/// <summary>Battery status, in volts.</summary>
public sealed record BatteryStatus
{
    public required double Value { get; init; }

    /// <summary>Only <c>V</c> in v1.</summary>
    public required string Unit { get; init; }
}

/// <summary>
/// NB-IoT radio diagnostics. Without these, "why did this device stop reporting" cannot be
/// answered after the fact.
/// </summary>
public sealed record SignalQuality
{
    /// <summary>Reference Signal Received Power, in dBm (-140 to -44).</summary>
    public int? Rsrp { get; init; }

    /// <summary>Reference Signal Received Quality, in dB (-20 to -3).</summary>
    public int? Rsrq { get; init; }
}
