namespace IIoT.TestClient.Provisioning;

/// <summary>
/// Where and how a device connects to the Device Provisioning Service over MQTT.
/// </summary>
/// <remarks>
/// From "Use MQTT to communicate with Azure IoT DPS"
/// (https://learn.microsoft.com/azure/iot-dps/iot-mqtt-connect-to-iot-dps): MQTT 3.1.1 on port
/// 8883 over TLS 1.2; the client ID is the registration ID; the username is
/// <c>{idScope}/registrations/{registration_id}/api-version=2019-03-31</c>; a device that
/// authenticates with an X.509 certificate sends no SAS token as its password. With X.509, the
/// registration ID is the certificate's CN, so it is also the device ID DPS registers in IoT Hub
/// (#48). DPS keeps no session, so every connection is a clean one.
/// </remarks>
public static class DpsEndpoint
{
    /// <summary>The global device provisioning endpoint, the same for every DPS instance.</summary>
    public const string GlobalHostName = "global.azure-devices-provisioning.net";

    /// <summary>MQTT over TLS.</summary>
    public const int Port = 8883;

    /// <summary>The DPS device API version the username names.</summary>
    public const string ApiVersion = "2019-03-31";

    /// <summary>The CONNECT username for <paramref name="registrationId"/> in the instance <paramref name="idScope"/>.</summary>
    public static string UserName(string idScope, string registrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);

        return $"{idScope}/registrations/{registrationId}/api-version={ApiVersion}";
    }
}
