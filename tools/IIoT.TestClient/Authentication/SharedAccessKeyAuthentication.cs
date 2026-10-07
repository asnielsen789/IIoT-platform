using MQTTnet;

namespace IIoT.TestClient.Authentication;

/// <summary>
/// Authenticates with the device identity's symmetric key, as registered in the IoT Hub identity
/// registry.
/// </summary>
/// <remarks>
/// This is the convenient mechanism, not the intended one. The platform's device authentication is
/// X.509 mutual TLS; a shared access key exists here so the client can be exercised against a hub
/// before certificate provisioning is in place. The key is held in memory only — it is read from
/// the command line, the environment or a runtime-supplied devices file, and never written
/// anywhere.
/// </remarks>
public sealed class SharedAccessKeyAuthentication : IDeviceAuthentication
{
    private readonly string _base64Key;

    public SharedAccessKeyAuthentication(string deviceId, string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);

        DeviceId = deviceId;
        _base64Key = base64Key;
    }

    public string DeviceId { get; }

    public string? CreatePassword(string hostName, DateTimeOffset expiresAt) =>
        SasToken.Create(SasToken.DeviceResourceUri(hostName, DeviceId), _base64Key, expiresAt);

    /// <summary>Nothing to add: the hub is authenticated by its server certificate only.</summary>
    public void ConfigureTls(MqttClientTlsOptionsBuilder tls)
    {
    }
}
