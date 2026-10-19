using Azure.Core;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Common.Exceptions;

namespace IIoT.DeviceCa.IotHub;

/// <summary>The identity registry of an IoT hub, through the IoT Hub service SDK.</summary>
/// <remarks>
/// <para>
/// Authenticates with Microsoft Entra ID through <see cref="RegistryManager.Create(string, TokenCredential, HttpTransportSettings)"/>,
/// the SDK's documented way to use a <see cref="TokenCredential"/>: no hub-level shared access key
/// is read or needed. The operator needs the data-plane role IoT Hub Registry Contributor on the
/// hub.
/// </para>
/// <para>
/// A device registered here has authentication type <see cref="AuthenticationType.CertificateAuthority"/>
/// and no key or thumbprint: IoT Hub authenticates it by its certificate chaining to a verified CA
/// certificate, with <c>CN=&lt;deviceId&gt;</c> as the subject. Nothing secret about the device is
/// stored in the hub.
/// </para>
/// </remarks>
public sealed class IotHubDeviceRegistry : IDeviceRegistry, IDisposable
{
    /// <summary>The suffix a bare hub name gains, as in the test client.</summary>
    public const string PublicCloudSuffix = ".azure-devices.net";

    private readonly RegistryManager _registry;

    public IotHubDeviceRegistry(string hostName, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentNullException.ThrowIfNull(credential);

        Name = hostName;
        _registry = RegistryManager.Create(hostName, credential);
    }

    public string Name { get; }

    /// <summary>
    /// Turns <c>--hub</c> into a host name: a bare name gains <c>.azure-devices.net</c>; anything with
    /// a dot is used verbatim.
    /// </summary>
    public static string ResolveHostName(string hubNameOrHostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hubNameOrHostName);

        var value = hubNameOrHostName.Trim();
        return value.Contains('.', StringComparison.Ordinal) ? value : value + PublicCloudSuffix;
    }

    public async Task<RegisteredDevice?> GetAsync(string deviceId, CancellationToken cancellationToken)
    {
        Device? device;
        try
        {
            device = await _registry.GetDeviceAsync(deviceId, cancellationToken);
        }
        catch (DeviceNotFoundException)
        {
            return null;
        }

        if (device is null)
            return null;

        var type = device.Authentication?.Type ?? AuthenticationType.None;
        return new RegisteredDevice(device.Id, type.ToString(), type == AuthenticationType.CertificateAuthority);
    }

    public async Task RegisterCertificateAuthorityDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        var device = new Device(deviceId)
        {
            Authentication = new AuthenticationMechanism { Type = AuthenticationType.CertificateAuthority },
            Status = DeviceStatus.Enabled
        };

        try
        {
            await _registry.AddDeviceAsync(device, cancellationToken);
        }
        catch (DeviceAlreadyExistsException ex)
        {
            throw new DeviceAlreadyRegisteredException(
                $"Device '{deviceId}' was registered in {Name} while this certificate was being issued.", ex);
        }
    }

    public void Dispose() => _registry.Dispose();
}
