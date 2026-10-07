using MQTTnet;

namespace IIoT.TestClient.Authentication;

/// <summary>
/// How one simulated device proves its identity to IoT Hub.
/// </summary>
/// <remarks>
/// Split out so X.509 client certificates can be added as a second implementation without
/// touching the connection or publishing code — see
/// https://github.com/asnielsen789/IIoT-platform/issues/11. The two mechanisms differ in exactly
/// two places, and those are the two members here: a SAS device supplies a password and leaves TLS
/// alone, while a certificate device supplies no password and contributes a client certificate.
/// The MQTT username is identical for both and is therefore not part of this interface.
/// </remarks>
public interface IDeviceAuthentication
{
    /// <summary>The registered IoT Hub device identity. Also the MQTT client id.</summary>
    string DeviceId { get; }

    /// <summary>
    /// The CONNECT password, or <see langword="null"/> when the mechanism does not use one.
    /// </summary>
    string? CreatePassword(string hostName, DateTimeOffset expiresAt);

    /// <summary>
    /// Adds whatever the mechanism needs to the TLS options. Called after the transport-level
    /// defaults (TLS on, target host, minimum protocol version) have been set.
    /// </summary>
    void ConfigureTls(MqttClientTlsOptionsBuilder tls);
}
