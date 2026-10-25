using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IIoT.TestClient.Authentication;

/// <summary>
/// A new device key pair and the certificate signing request for it, as device firmware would
/// produce them on first start.
/// </summary>
/// <remarks>
/// <para>
/// ADR-009: the device generates its own key, and the private key never leaves the device. The
/// CA receives only the CSR, a PKCS#10 request whose signature proves possession of the key.
/// </para>
/// <para>
/// EC P-256 with SHA-256, the only key type the device CA issues for (<c>IIoT.DeviceCa</c> refuses
/// anything else). The request asks for <c>CN=&lt;deviceId&gt;</c> and nothing more: the CA sets
/// the subject and every extension itself and ignores whatever else a CSR asks for, so requesting
/// more would only make the request larger.
/// </para>
/// </remarks>
/// <param name="DeviceId">The device the key belongs to.</param>
/// <param name="PrivateKeyPem">The private key, unencrypted PKCS#8 PEM. Secret.</param>
/// <param name="CsrPem">The certificate signing request, PEM. Public.</param>
public sealed record DeviceCsr(string DeviceId, string PrivateKeyPem, string CsrPem)
{
    /// <summary>Generates a new key pair for <paramref name="deviceId"/> and signs a CSR with it.</summary>
    public static DeviceCsr Create(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // The builder encodes the value as it is. Device IDs may contain ',', '=' and '\'', which a
        // distinguished-name string would have to escape.
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(deviceId);

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        return new DeviceCsr(deviceId, key.ExportPkcs8PrivateKeyPem(), request.CreateSigningRequestPem());
    }

    /// <summary>Kept out of <see cref="object.ToString"/>, so the key cannot reach a log by accident.</summary>
    public override string ToString() => $"DeviceCsr {{ DeviceId = {DeviceId} }}";
}
