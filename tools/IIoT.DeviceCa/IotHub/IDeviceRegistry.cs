namespace IIoT.DeviceCa.IotHub;

/// <summary>The IoT Hub identity registry, as far as issuing a certificate needs it.</summary>
/// <remarks>
/// Implemented over the IoT Hub service SDK by <see cref="IotHubDeviceRegistry"/>. Split out so
/// the issuing rules (never touch a device that authenticates some other way) are tested without
/// a hub.
/// </remarks>
public interface IDeviceRegistry
{
    /// <summary>A display name for messages, such as the hub's host name.</summary>
    string Name { get; }

    /// <summary>Looks up a device, or returns <see langword="null"/> if it is not registered.</summary>
    Task<RegisteredDevice?> GetAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a new device that authenticates with a certificate from a registered CA.
    /// </summary>
    /// <exception cref="DeviceAlreadyRegisteredException">The device exists already.</exception>
    Task RegisterCertificateAuthorityDeviceAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>A device as the identity registry holds it.</summary>
/// <param name="DeviceId">The device ID.</param>
/// <param name="AuthenticationType">The registry's name for how the device authenticates.</param>
/// <param name="IsCertificateAuthority">
/// True when the device authenticates with a certificate from a registered CA, the mechanism this
/// tool issues for.
/// </param>
public sealed record RegisteredDevice(string DeviceId, string AuthenticationType, bool IsCertificateAuthority);

/// <summary>A device was registered between the lookup and the registration.</summary>
public sealed class DeviceAlreadyRegisteredException : Exception
{
    public DeviceAlreadyRegisteredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
