namespace IIoT.DeviceCa.Certificates;

/// <summary>IoT Hub's rules for a device identity.</summary>
/// <remarks>
/// <para>
/// The device ID becomes the certificate's subject, <c>CN=&lt;deviceId&gt;</c>, which IoT Hub
/// requires for X.509 CA authentication, and then the registered identity. A certificate whose CN
/// IoT Hub would refuse as a device ID can never authenticate, so the ID is checked before anything
/// is signed.
/// </para>
/// <para>
/// From "Device identity properties" in
/// https://learn.microsoft.com/azure/iot-hub/iot-hub-devguide-identity-registry: "A case-sensitive
/// string (up to 128 characters long) of ASCII 7-bit alphanumeric characters plus certain special
/// characters: <c>- . % _ * ? ! ( ) , : = @ $ '</c>. The special characters <c>+ #</c> aren't
/// supported."
/// </para>
/// </remarks>
public static class DeviceId
{
    /// <summary>The longest device ID IoT Hub accepts.</summary>
    public const int MaxLength = 128;

    /// <summary>The non-alphanumeric characters IoT Hub accepts in a device ID.</summary>
    public const string SpecialCharacters = "-.%_*?!(),:=@$'";

    /// <summary>
    /// Returns why <paramref name="deviceId"/> is not a valid IoT Hub device ID, or
    /// <see langword="null"/> when it is.
    /// </summary>
    public static string? Problem(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "The device ID is empty.";

        if (deviceId.Length > MaxLength)
            return $"The device ID is {deviceId.Length} characters long; IoT Hub allows at most {MaxLength}.";

        foreach (var character in deviceId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && !SpecialCharacters.Contains(character, StringComparison.Ordinal))
            {
                return $"The device ID contains '{character}' (U+{(int)character:X4}). IoT Hub allows ASCII " +
                       $"letters, digits and {SpecialCharacters}.";
            }
        }

        return null;
    }

    /// <summary>Throws unless <paramref name="deviceId"/> is a valid IoT Hub device ID.</summary>
    /// <exception cref="ArgumentException">The ID breaks IoT Hub's rules.</exception>
    public static void Validate(string? deviceId)
    {
        if (Problem(deviceId) is { } problem)
            throw new ArgumentException(problem, nameof(deviceId));
    }
}
