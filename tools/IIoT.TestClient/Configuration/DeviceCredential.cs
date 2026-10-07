namespace IIoT.TestClient.Configuration;

/// <summary>One device identity and its symmetric key.</summary>
/// <param name="DeviceId">The identity as registered in IoT Hub.</param>
/// <param name="Key">The device's base64 shared access key.</param>
/// <remarks>
/// Supplied at runtime only — command line, environment, or a devices file the operator points at.
/// Nothing in this repository holds a key, and nothing in this client writes one anywhere.
/// </remarks>
public sealed record DeviceCredential(string DeviceId, string Key);
