namespace IIoT.TestClient.IotHub;

/// <summary>Turns whatever the user typed for <c>--hub</c> into a hostname.</summary>
public static class IotHubHost
{
    /// <summary>The suffix IoT Hub appends to a hub name in the public Azure cloud.</summary>
    public const string PublicCloudSuffix = ".azure-devices.net";

    /// <summary>
    /// Returns a full hostname. A bare hub name gains the public-cloud suffix; anything that
    /// already contains a dot is taken verbatim, so a sovereign or private cloud hostname is
    /// still usable without a code change.
    /// </summary>
    public static string Resolve(string hubNameOrHostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hubNameOrHostName);

        var value = hubNameOrHostName.Trim();
        return value.Contains('.', StringComparison.Ordinal) ? value : value + PublicCloudSuffix;
    }
}
