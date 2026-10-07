using System.Security.Authentication;
using System.Text;
using IIoT.TestClient.Authentication;
using MQTTnet;
using MQTTnet.Formatter;

namespace IIoT.TestClient.IotHub;

/// <summary>
/// Builds the MQTT client options for IoT Hub's device endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is dictated by IoT Hub rather than chosen: port 8883, MQTT 3.1.1 (the hub
/// supports no later version on this endpoint), the device id as client id, and the username and
/// password shapes documented at
/// https://learn.microsoft.com/azure/iot-hub/iot-mqtt-connect-to-iot-hub.
/// </para>
/// <para>
/// Building the options is separated from connecting so a test can assert what would be sent in
/// the CONNECT packet without a hub to connect to.
/// </para>
/// </remarks>
public static class DeviceConnection
{
    /// <summary>IoT Hub's MQTT port. The hub refuses plaintext MQTT on 1883 entirely.</summary>
    public const int Port = 8883;

    /// <summary>
    /// The API version in the username. IoT Hub documents that omitting it causes undefined
    /// behaviour, and a wrong value fails the connection without a useful reason.
    /// </summary>
    public const string ApiVersion = "2021-04-12";

    /// <summary>Builds the CONNECT username.</summary>
    public static string UserName(string hostName, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return $"{hostName}/{deviceId}/?api-version={ApiVersion}";
    }

    /// <summary>Builds the client options for one simulated device.</summary>
    /// <param name="hostName">The resolved hub hostname.</param>
    /// <param name="authentication">How this device proves its identity.</param>
    /// <param name="sasExpiresAt">When a generated credential should expire.</param>
    public static MqttClientOptions Build(
        string hostName,
        IDeviceAuthentication authentication,
        DateTimeOffset sasExpiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentNullException.ThrowIfNull(authentication);

        var password = authentication.CreatePassword(hostName, sasExpiresAt);

        return new MqttClientOptionsBuilder()
            .WithTcpServer(hostName, Port)
            .WithClientId(authentication.DeviceId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromMinutes(4))

            // A zero-length password rather than null: X.509 authentication (issue #11) supplies no
            // password at all, and IoT Hub ignores the field in that case.
            .WithCredentials(
                UserName(hostName, authentication.DeviceId),
                password is null ? [] : Encoding.UTF8.GetBytes(password))
            .WithTlsOptions(tls =>
            {
                tls.UseTls(true)
                    .WithTargetHost(hostName)
                    .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);

                // The mechanism gets the last word, so a certificate credential can add a client
                // certificate here without this method changing.
                authentication.ConfigureTls(tls);
            })
            .Build();
    }
}
