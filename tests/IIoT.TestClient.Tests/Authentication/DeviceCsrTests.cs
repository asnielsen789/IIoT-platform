using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.TestClient.Authentication;

namespace IIoT.TestClient.Tests.Authentication;

/// <summary>
/// The key pair and CSR a simulated device makes. The decisive check is the device CA's own:
/// <see cref="DeviceCertificateRequest.LoadPem"/> is what <c>IIoT.DeviceCa issue</c> runs on a CSR.
/// </summary>
public class DeviceCsrTests
{
    private const string DeviceId = "tank-000001";

    [Fact]
    public void Create_ProducesACsrTheDeviceCaAccepts()
    {
        var csr = DeviceCsr.Create(DeviceId);

        // Verifies the CSR's signature and requires an EC P-256 key on a named curve.
        var request = DeviceCertificateRequest.LoadPem(csr.CsrPem);

        using var key = ECDsa.Create();
        key.ImportFromPem(csr.PrivateKeyPem);
        Assert.Equal(
            key.ExportSubjectPublicKeyInfo(),
            request.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Create_SignsTheRequestWithTheNewKey()
    {
        var csr = DeviceCsr.Create(DeviceId);

        // The default load verifies the self-signature: proof of possession of the private key.
        var request = CertificateRequest.LoadSigningRequestPem(csr.CsrPem, HashAlgorithmName.SHA256);

        Assert.Equal(DeviceCertificateRequest.EcPublicKeyOid, request.PublicKey.Oid.Value);
        using var key = ECDsa.Create();
        key.ImportFromPem(csr.PrivateKeyPem);
        Assert.Equal(256, key.KeySize);
    }

    [Theory]
    [InlineData("tank-000001")]
    [InlineData("tank,with=special'chars")]
    [InlineData("site:7(a)")]
    public void Create_AsksForTheDeviceIdAsCommonName(string deviceId)
    {
        var csr = DeviceCsr.Create(deviceId);

        var request = CertificateRequest.LoadSigningRequestPem(csr.CsrPem, HashAlgorithmName.SHA256);
        var name = Assert.Single(request.SubjectName.EnumerateRelativeDistinguishedNames());

        Assert.Equal(X509CertificateAuthentication.CommonNameOid, name.GetSingleElementType().Value);
        Assert.Equal(deviceId, name.GetSingleElementValue());
    }

    [Fact]
    public void Create_WritesAnUnencryptedPkcs8Key()
    {
        var csr = DeviceCsr.Create(DeviceId);

        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", csr.PrivateKeyPem, StringComparison.Ordinal);
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", csr.CsrPem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", csr.CsrPem, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_MakesANewKeyEveryTime()
    {
        Assert.NotEqual(DeviceCsr.Create(DeviceId).PrivateKeyPem, DeviceCsr.Create(DeviceId).PrivateKeyPem);
    }

    [Fact]
    public void ToString_LeavesTheKeyOut()
    {
        var csr = DeviceCsr.Create(DeviceId);

        Assert.DoesNotContain("PRIVATE KEY", csr.ToString(), StringComparison.Ordinal);
        Assert.Contains(DeviceId, csr.ToString(), StringComparison.Ordinal);
    }
}
