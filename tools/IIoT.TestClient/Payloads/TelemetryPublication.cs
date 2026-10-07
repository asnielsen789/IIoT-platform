namespace IIoT.TestClient.Payloads;

/// <summary>One message as it will go on the wire.</summary>
/// <param name="FixtureName">Which fixture it came from, for logging.</param>
/// <param name="Topic">The full publish topic, property bag included.</param>
/// <param name="Payload">The UTF-8 body.</param>
public sealed record TelemetryPublication(string FixtureName, string Topic, byte[] Payload);
