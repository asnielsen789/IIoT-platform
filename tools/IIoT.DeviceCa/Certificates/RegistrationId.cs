namespace IIoT.DeviceCa.Certificates;

/// <summary>The Device Provisioning Service's rules for a registration ID.</summary>
/// <remarks>
/// <para>
/// In an X.509 enrollment group, the device certificate's CN becomes the registration ID, and the
/// registration ID becomes the device ID DPS registers in IoT Hub (#48). These rules are stricter
/// than IoT Hub's (<see cref="DeviceId"/>): fewer special characters, a rule for the last
/// character, and at most 64 characters, the longest CN an X.509 certificate allows. A certificate
/// whose CN breaks them can never provision, so the ID is checked before anything is signed.
/// </para>
/// <para>
/// From "Enrollment group" and "Registration ID" in
/// https://learn.microsoft.com/azure/iot-dps/concepts-service: "case-insensitive strings of
/// alphanumeric characters plus the special characters: <c>- . _ :</c>. The last character must be
/// alphanumeric or dash (<c>-</c>)", and "in X.509 enrollment groups, because the maximum length of
/// the subject common name in an X.509 certificate is 64 characters, the registration IDs are
/// limited to 64 characters."
/// </para>
/// </remarks>
public static class RegistrationId
{
    /// <summary>The longest registration ID an X.509 enrollment group accepts.</summary>
    public const int MaxLength = 64;

    /// <summary>The non-alphanumeric characters DPS accepts in a registration ID.</summary>
    public const string SpecialCharacters = "-._:";

    /// <summary>
    /// Returns why <paramref name="deviceId"/> cannot be a DPS registration ID, or
    /// <see langword="null"/> when it can.
    /// </summary>
    public static string? Problem(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "The device ID is empty.";

        if (deviceId.Length > MaxLength)
        {
            return $"The device ID is {deviceId.Length} characters long; as a DPS registration ID, the " +
                   $"certificate's CN, it can be at most {MaxLength}.";
        }

        foreach (var character in deviceId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && !SpecialCharacters.Contains(character, StringComparison.Ordinal))
            {
                return $"The device ID contains '{character}' (U+{(int)character:X4}). A DPS registration ID " +
                       $"allows ASCII letters, digits and {SpecialCharacters}.";
            }
        }

        var last = deviceId[^1];
        if (!char.IsAsciiLetterOrDigit(last) && last != '-')
            return $"The device ID ends in '{last}'. A DPS registration ID must end in a letter, a digit or '-'.";

        return null;
    }
}
