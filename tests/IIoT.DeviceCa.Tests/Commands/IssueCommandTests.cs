using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Commands;
using IIoT.DeviceCa.IotHub;
using IIoT.DeviceCa.KeyVault;
using IIoT.DeviceCa.Tests.TestDoubles;

namespace IIoT.DeviceCa.Tests.Commands;

/// <summary>
/// The rules around issuing: what is written, what is registered, and that a refusal changes
/// nothing — no file, no registration, no signature.
/// </summary>
public sealed class IssueCommandTests : IDisposable
{
    private const string DeviceId = "tank-000002";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeCaVault _vault = new();
    private readonly FakeDeviceRegistry _registry = new();
    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _directory = Directory.CreateTempSubdirectory("iiot-deviceca-").FullName;
    private readonly string _csrFile;

    public IssueCommandTests()
    {
        new InitCommand(_vault, new FixedTime(Now), TextWriter.Null).RunAsync(false, CancellationToken.None)
            .GetAwaiter().GetResult();

        _csrFile = Path.Combine(_directory, "device.csr.pem");
        File.WriteAllText(_csrFile, Csr.Create(_deviceKey, "CN=whatever-the-device-asked-for"));
    }

    public void Dispose()
    {
        _vault.Dispose();
        _deviceKey.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Run_WritesTheCertificateAndAChainWithoutTheRoot()
    {
        var issued = await Issue();

        Assert.Equal(Path.Combine(_directory, "out", $"{DeviceId}.cert.pem"), issued.CertificatePath);
        Assert.Equal(Path.Combine(_directory, "out", $"{DeviceId}.chain.pem"), issued.ChainPath);

        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(issued.CertificatePath));
        Assert.Equal($"CN={DeviceId}", certificate.Subject);

        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(File.ReadAllText(issued.ChainPath));

        // Device first, then the issuing CA IoT Hub has registered, and no root: it would only add
        // bytes to every handshake.
        Assert.Equal(2, chain.Count);
        Assert.Equal(certificate.Thumbprint, chain[0].Thumbprint);
        Assert.Equal(CertificateAuthority.IssuingSubject, chain[1].Subject);
        Assert.DoesNotContain(chain.Cast<X509Certificate2>(), c => c.Subject == CertificateAuthority.RootSubject);

        using var root = X509Certificate2.CreateFromPem(_vault.Secrets[CaNames.RootCertificate]);
        CertificateAuthority.VerifyChain(chain[0], root, chain[1], Now);

        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(issued.ChainPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RegistersANewDeviceAsCaAuthenticated()
    {
        var issued = await Issue();

        Assert.True(issued.Registered);
        Assert.Equal([DeviceId], _registry.Registrations);
    }

    [Fact]
    public async Task Run_ForADeviceAlreadyCaAuthenticated_IssuesAndLeavesTheRegistration()
    {
        _registry.AddCaDevice(DeviceId);

        var issued = await Issue();

        Assert.False(issued.Registered);
        Assert.Empty(_registry.Registrations);
        Assert.True(File.Exists(issued.ChainPath));
    }

    [Fact]
    public async Task Run_ForADeviceWithAnotherAuthenticationType_RefusesAndChangesNothing()
    {
        _registry.AddSasDevice(DeviceId);

        var exception = await Assert.ThrowsAsync<CaRefusedException>(() => Issue());

        Assert.Contains("Sas", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was issued or changed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_registry.Registrations);
        Assert.Equal("Sas", _registry.Devices[DeviceId].AuthenticationType);
        Assert.Equal(0, _vault.IssuingKey.Signatures);
        Assert.False(Directory.Exists(Path.Combine(_directory, "out")));
    }

    [Fact]
    public async Task Run_AnInvalidDeviceId_IsRefusedBeforeTheHubIsAsked()
    {
        await Assert.ThrowsAsync<CaRefusedException>(() => Issue(deviceId: "tank#2"));

        Assert.Equal(0, _registry.Lookups);
        Assert.Equal(0, _vault.IssuingKey.Signatures);
    }

    [Fact]
    public async Task Run_ACsrWithABrokenSignature_IsRefusedBeforeTheHubIsAsked()
    {
        File.WriteAllText(_csrFile, Csr.WithBrokenSignature(Csr.Create(_deviceKey)));

        var exception = await Assert.ThrowsAsync<CaRefusedException>(() => Issue());

        Assert.Contains("signature", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _registry.Lookups);
        Assert.Empty(_registry.Registrations);
    }

    [Fact]
    public async Task Run_AnRsaCsr_IsRefused()
    {
        File.WriteAllText(_csrFile, Csr.Rsa());

        await Assert.ThrowsAsync<CaRefusedException>(() => Issue());

        Assert.Equal(0, _registry.Lookups);
    }

    [Fact]
    public async Task Run_AnExistingCertificateFile_IsNeverOverwritten()
    {
        var output = Directory.CreateDirectory(Path.Combine(_directory, "out")).FullName;
        var existing = Path.Combine(output, $"{DeviceId}.cert.pem");
        File.WriteAllText(existing, "a certificate a device still uses");

        await Assert.ThrowsAsync<CaRefusedException>(() => Issue());

        Assert.Equal("a certificate a device still uses", File.ReadAllText(existing));
        Assert.Equal(0, _registry.Lookups);
        Assert.Equal(0, _vault.IssuingKey.Signatures);
    }

    [Fact]
    public async Task Run_WithoutInit_IsRefused()
    {
        _vault.Secrets.Clear();

        var exception = await Assert.ThrowsAsync<CaRefusedException>(() => Issue());

        Assert.Contains("init", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_registry.Registrations);
    }

    [Fact]
    public async Task Run_WhenTheIssuingCertificateIsNotTheVaultKeys_Fails()
    {
        // The issuing key no longer matches the stored certificate, say after the keys were
        // recreated without running init again: nothing may be signed with it.
        using var other = new TestCa(Now);
        _vault.Secrets[CaNames.RootCertificate] = other.Root.ExportCertificatePem();
        _vault.Secrets[CaNames.IssuingCertificate] = other.Issuing.ExportCertificatePem();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue());

        Assert.Equal(0, _vault.IssuingKey.Signatures);
        Assert.Empty(_registry.Registrations);
    }

    [Fact]
    public async Task Run_WhenTheDeviceAppearsMeanwhileAsCaAuthenticated_Succeeds()
    {
        _registry.RegisteredConcurrently = new RegisteredDevice(DeviceId, "CertificateAuthority", true);

        var issued = await Issue();

        Assert.False(issued.Registered);
    }

    [Fact]
    public async Task Run_WhenTheDeviceAppearsMeanwhileWithSas_SaysSo()
    {
        _registry.RegisteredConcurrently = new RegisteredDevice(DeviceId, "Sas", false);

        var exception = await Assert.ThrowsAsync<CaRefusedException>(() => Issue());

        Assert.Contains("Sas", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tank-000002", "tank-000002")]
    [InlineData("site:7/tank?1", "site_7_tank_1")]
    [InlineData("a.b_c-d", "a.b_c-d")]
    public void FileStem_KeepsSafeCharactersOnly(string deviceId, string expected)
    {
        Assert.Equal(expected, IssueCommand.FileStem(deviceId));
    }

    private Task<IssuedCertificate> Issue(string deviceId = DeviceId) =>
        new IssueCommand(_vault, _registry, new FixedTime(Now), TextWriter.Null)
            .RunAsync(_csrFile, deviceId, Path.Combine(_directory, "out"), CancellationToken.None);
}
