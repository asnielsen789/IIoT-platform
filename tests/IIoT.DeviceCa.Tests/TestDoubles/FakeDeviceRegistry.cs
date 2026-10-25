using IIoT.DeviceCa.IotHub;

namespace IIoT.DeviceCa.Tests.TestDoubles;

/// <summary>An in-memory identity registry that records what was registered.</summary>
public sealed class FakeDeviceRegistry : IDeviceRegistry
{
    public Dictionary<string, RegisteredDevice> Devices { get; } = new(StringComparer.Ordinal);

    public List<string> Registrations { get; } = [];

    public int Lookups { get; private set; }

    /// <summary>When set, registration fails as if this device appeared in the meantime.</summary>
    public RegisteredDevice? RegisteredConcurrently { get; set; }

    public string Name => "fake-hub.azure-devices.net";

    public void AddSasDevice(string deviceId) =>
        Devices[deviceId] = new RegisteredDevice(deviceId, "Sas", IsCertificateAuthority: false);

    public void AddCaDevice(string deviceId) =>
        Devices[deviceId] = new RegisteredDevice(deviceId, "CertificateAuthority", IsCertificateAuthority: true);

    public Task<RegisteredDevice?> GetAsync(string deviceId, CancellationToken cancellationToken)
    {
        Lookups++;
        return Task.FromResult(Devices.GetValueOrDefault(deviceId));
    }

    public Task RegisterCertificateAuthorityDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (RegisteredConcurrently is { } concurrent)
        {
            Devices[deviceId] = concurrent;
            throw new DeviceAlreadyRegisteredException("exists", new InvalidOperationException());
        }

        Registrations.Add(deviceId);
        Devices[deviceId] = new RegisteredDevice(deviceId, "CertificateAuthority", IsCertificateAuthority: true);
        return Task.CompletedTask;
    }
}
