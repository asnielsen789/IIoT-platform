namespace IIoT.Core.Tests.Fixtures;

/// <summary>
/// Builds a telemetry payload inside the test, for edge cases that are not contract examples.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a file in <c>docs/schemas/examples/</c>. Those are the contract's examples:
/// the test client replays every one of them against a real hub, and adding an edge case there
/// would make it part of what the contract shows a device should send.
/// </para>
/// <para>
/// The defaults make a minimal valid message. Timestamps are passed as the string the device
/// wrote; every other argument is raw JSON text, so a test states exactly what arrived on the wire
/// — <c>4711.0</c> and <c>4711</c> are different payloads.
/// </para>
/// </remarks>
internal static class InlinePayload
{
    public static string Telemetry(
        string schemaVersion = "1",
        string sentAt = "2026-09-14T06:00:12Z",
        string? sequence = null,
        string measuredAt = "2026-09-14T06:00:10Z",
        string value = "1432.5",
        string device = """{ "firmware": "1.4.2" }""",
        string? extraRootMember = null,
        string? extraMeasurementMember = null)
    {
        var root = new List<string>
        {
            $"\"schemaVersion\": {schemaVersion}",
            "\"messageId\": \"01J8F2K9QS4T7V0X3Z6B8N1M5E\"",
            $"\"sentAt\": \"{sentAt}\""
        };

        if (sequence is not null)
        {
            root.Add($"\"sequence\": {sequence}");
        }

        var measurement = new List<string>
        {
            "\"type\": \"level\"",
            $"\"value\": {value}",
            "\"unit\": \"mm\"",
            $"\"measuredAt\": \"{measuredAt}\""
        };

        if (extraMeasurementMember is not null)
        {
            measurement.Add(extraMeasurementMember);
        }

        root.Add($"\"measurements\": [ {{ {string.Join(", ", measurement)} }} ]");
        root.Add($"\"device\": {device}");

        if (extraRootMember is not null)
        {
            root.Add(extraRootMember);
        }

        return $"{{ {string.Join(", ", root)} }}";
    }
}
