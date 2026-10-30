using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IIoT.TestClient.Authentication;
using IIoT.TestClient.IotHub;
using IIoT.TestClient.Tests.TestDoubles;
using MQTTnet;

namespace IIoT.TestClient.Tests.Authentication;

/// <summary>
/// Loading a device certificate and what it contributes to the connection. Every certificate is
/// issued by the device CA's own code, for a key the client generated.
/// </summary>
public sealed class X509CertificateAuthenticationTests : IDisposable
{
    private const string DeviceId = "tank-000001";
    private const string HostName = "example-hub.azure-devices.net";

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly TestPki _pki = new(Now);
    private readonly DeviceCsr _csr = DeviceCsr.Create(DeviceId);
    private readonly X509Certificate2 _issued;

    public X509CertificateAuthenticationTests() => _issued = _pki.Issue(_csr);

    public void Dispose()
    {
        _issued.Dispose();
        _pki.Dispose();
    }

    [Fact]
    public void FromPem_TheChainFile_UsesTheDeviceCertificate()
    {
        using var authentication = FromPem(_pki.ChainPem(_issued));

        Assert.Equal(DeviceId, authentication.DeviceId);
        Assert.Equal(_issued.Thumbprint, authentication.Certificate.Thumbprint);
    }

    [Fact]
    public void FromPem_TheCertificateFile_IsEnoughOnItsOwn()
    {
        using var authentication = FromPem(_issued.ExportCertificatePem());

        Assert.Equal(_issued.Thumbprint, authentication.Certificate.Thumbprint);
    }

    [Fact]
    public void FromPem_KeepsAUsablePrivateKey()
    {
        // After the PKCS#12 round trip the key must still be there and still be the device's.
        using var authentication = FromPem(_pki.ChainPem(_issued));
        using var key = authentication.Certificate.GetECDsaPrivateKey();

        Assert.NotNull(key);
        var data = Encoding.UTF8.GetBytes("proof of possession");
        using var publicKey = authentication.Certificate.GetECDsaPublicKey();
        Assert.True(publicKey!.VerifyData(data, key!.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void CreatePassword_IsNull()
    {
        using var authentication = FromPem(_pki.ChainPem(_issued));

        Assert.Null(authentication.CreatePassword(HostName, Now.AddHours(1)));
    }

    [Fact]
    public void ConfigureTls_AddsTheDeviceCertificateOnly()
    {
        using var authentication = FromPem(_pki.ChainPem(_issued));
        var tls = new MqttClientTlsOptionsBuilder();

        authentication.ConfigureTls(tls);

        var certificate = Assert.Single(tls.Build().ClientCertificatesProvider.GetCertificates());
        Assert.Equal(_issued.Thumbprint, Assert.IsType<X509Certificate2>(certificate).Thumbprint);
    }

    [Fact]
    public void ConfigureTls_PicksTheDeviceCertificateWhateverCaNamesTheServerLists()
    {
        // Without the handler, .NET drops a certificate whose chain matches none of the listed
        // names and carries on with no client certificate at all.
        using var authentication = FromPem(_pki.ChainPem(_issued));
        var tls = new MqttClientTlsOptionsBuilder();
        authentication.ConfigureTls(tls);

        var selected = tls.Build().CertificateSelectionHandler(new MqttClientCertificateSelectionEventArgs(
            HostName, new X509CertificateCollection(), null, ["CN=Some Unrelated CA"], new MqttClientTcpOptions()));

        Assert.Same(authentication.Certificate, selected);
    }

    [Fact]
    public void Build_UsesTheSameUserNameAsAKeyAndAnEmptyPassword()
    {
        using var authentication = FromPem(_pki.ChainPem(_issued));

        var options = DeviceConnection.Build(HostName, authentication, Now.AddHours(1));

        Assert.Equal(DeviceId, options.ClientId);
        Assert.Equal(DeviceConnection.UserName(HostName, DeviceId), options.Credentials!.GetUserName(options));
        Assert.Empty(options.Credentials.GetPassword(options));

        var channel = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        Assert.True(channel.TlsOptions.UseTls);
        Assert.Equal(HostName, channel.TlsOptions.TargetHost);
        Assert.False(channel.TlsOptions.AllowUntrustedCertificates);
        Assert.Single(channel.TlsOptions.ClientCertificatesProvider.GetCertificates());
    }

    [Fact]
    public void FromPem_ACertificateForAnotherDevice_Fails()
    {
        var exception = Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            "tank-000002", _pki.ChainPem(_issued), _csr.PrivateKeyPem, Now));

        Assert.Contains("'tank-000001'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'tank-000002'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPem_ComparesTheDeviceIdCaseSensitively()
    {
        // IoT Hub device IDs are case-sensitive, so 'Tank-000001' is another device.
        Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            "Tank-000001", _pki.ChainPem(_issued), _csr.PrivateKeyPem, Now));
    }

    [Fact]
    public void FromPem_ACertificateWithoutACommonName_Fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("O=No Common Name", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));

        var exception = Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            DeviceId, certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), Now));

        Assert.Contains("no single CN", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPem_TheIssuingCaFirst_FailsOnTheCommonName()
    {
        // Certificates in the wrong order are caught by the CN rule before the key is even read.
        var reversed = _pki.Issuing.ExportCertificatePem() + "\n" + _issued.ExportCertificatePem();

        Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            DeviceId, reversed, _csr.PrivateKeyPem, Now));
    }

    [Fact]
    public void FromPem_AKeyThatIsNotTheCertificates_FailsWithoutEchoingIt()
    {
        var otherKey = DeviceCsr.Create(DeviceId).PrivateKeyPem;

        var exception = Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            DeviceId, _pki.ChainPem(_issued), otherKey, Now));

        Assert.Contains("no usable private key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PemBody(otherKey), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3 * 365)]
    public void FromPem_ACertificateNotValidNow_Fails(int days)
    {
        // Issued at Now with five minutes of backdating, valid for two years.
        var exception = Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            DeviceId, _pki.ChainPem(_issued), _csr.PrivateKeyPem, Now.AddDays(days)));

        Assert.Contains("not now", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a certificate")]
    public void FromPem_NoCertificate_Fails(string pem)
    {
        Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.FromPem(
            DeviceId, pem, _csr.PrivateKeyPem, Now));
    }

    [Fact]
    public void Load_AMissingFile_Fails()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"iiot-missing-{Guid.NewGuid():N}.pem");

        Assert.Throws<FileNotFoundException>(() => X509CertificateAuthentication.Load(DeviceId, missing, missing, Now));
    }

    [Fact]
    public void LoadByCommonName_TakesTheDeviceIdFromTheCertificate()
    {
        // provision has no device ID to give: the CN is the registration ID and becomes the device ID.
        WithFiles(_pki.ChainPem(_issued), _csr.PrivateKeyPem, (certificateFile, keyFile) =>
        {
            using var authentication = X509CertificateAuthentication.LoadByCommonName(certificateFile, keyFile, Now);

            Assert.Equal(DeviceId, authentication.DeviceId);
            Assert.Equal(_issued.Thumbprint, authentication.Certificate.Thumbprint);
        });
    }

    [Fact]
    public void LoadByCommonName_ACertificateWithoutACommonName_Fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("O=No Common Name", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));

        WithFiles(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), (certificateFile, keyFile) =>
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => X509CertificateAuthentication.LoadByCommonName(certificateFile, keyFile, Now));

            Assert.Contains("no single CN", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void LoadByCommonName_StillChecksTheKeyAndTheValidity()
    {
        WithFiles(_pki.ChainPem(_issued), DeviceCsr.Create(DeviceId).PrivateKeyPem, (certificateFile, keyFile) =>
            Assert.Throws<InvalidDataException>(() => X509CertificateAuthentication.LoadByCommonName(certificateFile, keyFile, Now)));

        WithFiles(_pki.ChainPem(_issued), _csr.PrivateKeyPem, (certificateFile, keyFile) =>
            Assert.Throws<InvalidDataException>(
                () => X509CertificateAuthentication.LoadByCommonName(certificateFile, keyFile, Now.AddDays(3 * 365))));
    }

    private static void WithFiles(string certificatePem, string keyPem, Action<string, string> test)
    {
        var directory = Directory.CreateTempSubdirectory("iiot-testclient-").FullName;
        try
        {
            var certificateFile = Path.Combine(directory, "device.chain.pem");
            var keyFile = Path.Combine(directory, "device.key.pem");
            File.WriteAllText(certificateFile, certificatePem);
            File.WriteAllText(keyFile, keyPem);

            test(certificateFile, keyFile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private X509CertificateAuthentication FromPem(string certificatePem) =>
        X509CertificateAuthentication.FromPem(DeviceId, certificatePem, _csr.PrivateKeyPem, Now);

    private static string PemBody(string pem) => pem.Split('\n')[1];
}
