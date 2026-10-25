using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IIoT.DeviceCa.Tests.TestDoubles;

/// <summary>Certificate signing requests as a device would produce them, and some it should not.</summary>
public static class Csr
{
    /// <summary>A P-256 CSR for <paramref name="subject"/>, signed by <paramref name="key"/>.</summary>
    public static string Create(ECDsa key, string subject = "CN=requested-by-device") =>
        new CertificateRequest(subject, key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

    /// <summary>A CSR for a key on another curve.</summary>
    public static string ForCurve(ECCurve curve)
    {
        using var key = ECDsa.Create(curve);
        return Create(key);
    }

    /// <summary>An RSA CSR.</summary>
    public static string Rsa()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=rsa-device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
    }

    /// <summary>
    /// <paramref name="pem"/> with one bit flipped in the last byte, which is part of the signature's
    /// s value: the request still parses, but its signature no longer verifies.
    /// </summary>
    public static string WithBrokenSignature(string pem)
    {
        var fields = PemEncoding.Find(pem);
        var der = Convert.FromBase64String(pem[fields.Base64Data]);

        der[^1] ^= 0x01;
        return PemEncoding.WriteString("CERTIFICATE REQUEST", der);
    }
}
