using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.TestClient.Authentication;

namespace IIoT.TestClient.Tests.TestDoubles;

/// <summary>
/// The device CA as <c>IIoT.DeviceCa</c> builds it, root and issuing CA, with local keys instead of
/// Key Vault ones; and a server certificate standing in for the hub's. Everything is generated per
/// instance, in memory: no key or certificate is committed anywhere.
/// </summary>
public sealed class TestPki : IDisposable
{
    public TestPki(DateTimeOffset now)
    {
        Now = now;
        RootKey = new LocalSigner("test-root");
        IssuingKey = new LocalSigner("test-issuing");
        Root = CertificateAuthority.CreateRoot(RootKey, now);
        Issuing = CertificateAuthority.CreateIssuing(IssuingKey, Root, RootKey, now);
    }

    public DateTimeOffset Now { get; }

    public LocalSigner RootKey { get; }

    public LocalSigner IssuingKey { get; }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Issuing { get; }

    /// <summary>
    /// Issues a certificate for the key in <paramref name="csr"/>, exactly as <c>IIoT.DeviceCa issue</c>
    /// does: the CA's own CSR check and certificate profile.
    /// </summary>
    public X509Certificate2 Issue(DeviceCsr csr, string? deviceId = null) =>
        CertificateAuthority.IssueDevice(
            DeviceCertificateRequest.LoadPem(csr.CsrPem), deviceId ?? csr.DeviceId, Issuing, IssuingKey, Now);

    /// <summary>The chain file <c>issue</c> writes: device certificate, then the issuing CA, no root.</summary>
    public string ChainPem(X509Certificate2 device) =>
        device.ExportCertificatePem() + "\n" + Issuing.ExportCertificatePem() + "\n";

    /// <summary>Verifies a device certificate as a relying party that holds the issuing CA would.</summary>
    public bool Verifies(X509Certificate2 device)
    {
        try
        {
            CertificateAuthority.VerifyChain(device, Root, Issuing, DateTimeOffset.UtcNow);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// A self-signed TLS server certificate for <c>localhost</c> and <c>127.0.0.1</c>, with its key
    /// in a form SChannel accepts (see <see cref="X509CertificateAuthentication"/>).
    /// </summary>
    public static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }

    public void Dispose()
    {
        Root.Dispose();
        Issuing.Dispose();
        RootKey.Dispose();
        IssuingKey.Dispose();
    }
}
