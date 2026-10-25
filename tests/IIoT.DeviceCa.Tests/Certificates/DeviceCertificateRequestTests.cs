using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Tests.TestDoubles;

namespace IIoT.DeviceCa.Tests.Certificates;

/// <summary>
/// What the CA accepts from a device: a CSR whose signature verifies, for an EC P-256 key. Anything
/// else is refused before a certificate is built.
/// </summary>
public class DeviceCertificateRequestTests
{
    [Fact]
    public void LoadPem_AValidP256Csr_YieldsItsKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = DeviceCertificateRequest.LoadPem(Csr.Create(key, "CN=tank-000002"));

        Assert.Equal(DeviceCertificateRequest.EcPublicKeyOid, request.PublicKey.Oid.Value);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), request.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.Equal("CN=tank-000002", request.RequestedSubject.Name);
    }

    [Fact]
    public void LoadPem_ABrokenSignature_IsRejected()
    {
        // Proof of possession: a CSR that its key did not sign, or that was altered afterwards, says
        // nothing about who holds the private key.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tampered = Csr.WithBrokenSignature(Csr.Create(key));

        var exception = Assert.Throws<InvalidDataException>(() => DeviceCertificateRequest.LoadPem(tampered));

        Assert.Contains("signature", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPem_AnRsaKey_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => DeviceCertificateRequest.LoadPem(Csr.Rsa()));

        Assert.Contains("P-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPem_AP384Key_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => DeviceCertificateRequest.LoadPem(Csr.ForCurve(ECCurve.NamedCurves.nistP384)));

        Assert.Contains("P-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPem_ACertificateInsteadOfACsr_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=not-a-csr", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        Assert.Throws<InvalidDataException>(() => DeviceCertificateRequest.LoadPem(certificate.ExportCertificatePem()));
    }

    [Fact]
    public void LoadPem_Garbage_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => DeviceCertificateRequest.LoadPem("not a certificate request"));
    }
}
