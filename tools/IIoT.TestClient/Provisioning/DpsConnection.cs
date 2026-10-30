using System.Security.Authentication;
using IIoT.TestClient.Authentication;
using MQTTnet;
using MQTTnet.Formatter;

namespace IIoT.TestClient.Provisioning;

/// <summary>
/// Builds the MQTT options for one device's connection to the Device Provisioning Service.
/// </summary>
/// <remarks>
/// The same TLS set-up as the IoT Hub connection (<see cref="IotHub.DeviceConnection"/>), with the
/// device certificate added by <see cref="X509CertificateAuthentication"/>. What differs is DPS's:
/// the client ID is the registration ID, the username names the ID scope and the DPS API version,
/// and there is no password at all, because "SAS token passwords aren't required" with X.509
/// (<see cref="DpsEndpoint"/>). DPS treats every session as clean.
/// </remarks>
public static class DpsConnection
{
    /// <summary>Builds the options for <paramref name="authentication"/>'s device in the DPS instance <paramref name="idScope"/>.</summary>
    /// <param name="hostName">The device provisioning host: <see cref="DpsEndpoint.GlobalHostName"/>, or a test server.</param>
    /// <param name="idScope">The DPS instance's ID scope.</param>
    /// <param name="authentication">The device certificate; its device ID is the registration ID.</param>
    public static MqttClientOptions Build(string hostName, string idScope, X509CertificateAuthentication authentication)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(idScope);
        ArgumentNullException.ThrowIfNull(authentication);

        return new MqttClientOptionsBuilder()
            .WithTcpServer(hostName, DpsEndpoint.Port)
            .WithClientId(authentication.DeviceId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithCredentials(new MqttClientCredentials(DpsEndpoint.UserName(idScope, authentication.DeviceId), null))
            .WithTlsOptions(tls =>
            {
                tls.UseTls(true)
                    .WithTargetHost(hostName)
                    .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);

                authentication.ConfigureTls(tls);
            })
            .Build();
    }
}
