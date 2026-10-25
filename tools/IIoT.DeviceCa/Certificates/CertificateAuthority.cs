using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IIoT.DeviceCa.Certificates;

/// <summary>
/// The certificate profiles of the device CA (ADR-009): a self-signed root CA, an issuing CA it
/// signs, and device certificates the issuing CA signs.
/// </summary>
/// <remarks>
/// <para>
/// Plain RFC 5280 certificates with no Azure-specific content, so moving the keys to an HSM or the
/// issuing CA to a commercial provider replaces the signer, not the devices' trust model. Every
/// method returns a certificate without a private key: the CA keys stay with their signer.
/// </para>
/// <para>
/// Every certificate starts five minutes in the past. A device, or a hub, whose clock runs slightly
/// behind would otherwise see a brand-new certificate as not yet valid.
/// </para>
/// </remarks>
public static class CertificateAuthority
{
    /// <summary>Subject of the root CA certificate.</summary>
    public const string RootSubject = "CN=IIoT Device Root CA";

    /// <summary>Subject of the issuing CA certificate.</summary>
    public const string IssuingSubject = "CN=IIoT Device Issuing CA";

    /// <summary>Root CA validity.</summary>
    public const int RootValidityYears = 20;

    /// <summary>Issuing CA validity, capped at the root's.</summary>
    public const int IssuingValidityYears = 10;

    /// <summary>Device certificate validity, capped at the issuing CA's (ADR-009: about two years).</summary>
    public const int DeviceValidityYears = 2;

    /// <summary>id-kp-clientAuth (RFC 5280, section 4.2.1.12).</summary>
    public const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>How far before "now" every certificate's validity starts, for clock skew.</summary>
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    /// <summary>Builds the self-signed root CA certificate.</summary>
    public static X509Certificate2 CreateRoot(ICertificateSigner rootKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rootKey);

        var generator = new SignerSignatureGenerator(rootKey);
        var subject = new X500DistinguishedName(RootSubject);
        var request = new CertificateRequest(subject, generator.PublicKey, HashAlgorithmName.SHA256);

        // A CA with no path length limit of its own: the issuing CA below it carries the limit.
        request.CertificateExtensions.Add(X509BasicConstraintsExtension.CreateForCertificateAuthority());
        request.CertificateExtensions.Add(CaKeyUsage());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.Create(
            subject, generator, now - ClockSkewAllowance, now.AddYears(RootValidityYears), SerialNumber.Create());
    }

    /// <summary>Builds the issuing CA certificate, signed by the root CA key.</summary>
    /// <exception cref="InvalidOperationException">
    /// The root key does not belong to the root certificate, or the root certificate is not valid now.
    /// </exception>
    public static X509Certificate2 CreateIssuing(
        ICertificateSigner issuingKey,
        X509Certificate2 rootCertificate,
        ICertificateSigner rootKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuingKey);
        ArgumentNullException.ThrowIfNull(rootCertificate);
        ArgumentNullException.ThrowIfNull(rootKey);

        RequireSigningCa(rootCertificate, rootKey, now);

        var issuingPublicKey = new SignerSignatureGenerator(issuingKey).PublicKey;
        var request = new CertificateRequest(
            new X500DistinguishedName(IssuingSubject), issuingPublicKey, HashAlgorithmName.SHA256);

        // Path length 0: the issuing CA may sign end-entity certificates only, never another CA.
        request.CertificateExtensions.Add(X509BasicConstraintsExtension.CreateForCertificateAuthority(0));
        request.CertificateExtensions.Add(CaKeyUsage());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(AuthorityKeyIdentifier(rootCertificate));

        return request.Create(
            rootCertificate.SubjectName,
            new SignerSignatureGenerator(rootKey),
            now - ClockSkewAllowance,
            Earlier(now.AddYears(IssuingValidityYears), NotAfter(rootCertificate)),
            SerialNumber.Create());
    }

    /// <summary>Issues a device certificate for the key in <paramref name="request"/>.</summary>
    /// <param name="request">The device's verified CSR. Only its public key is used.</param>
    /// <param name="deviceId">
    /// The IoT Hub device ID. It becomes the subject, <c>CN=&lt;deviceId&gt;</c>, whatever subject the
    /// CSR asked for.
    /// </param>
    /// <param name="issuingCertificate">The issuing CA certificate.</param>
    /// <param name="issuingKey">The issuing CA key.</param>
    /// <param name="now">The issuance time.</param>
    /// <exception cref="ArgumentException">The device ID breaks IoT Hub's rules.</exception>
    /// <exception cref="InvalidOperationException">
    /// The issuing key does not belong to the issuing certificate, or the issuing certificate is not
    /// valid now.
    /// </exception>
    public static X509Certificate2 IssueDevice(
        DeviceCertificateRequest request,
        string deviceId,
        X509Certificate2 issuingCertificate,
        ICertificateSigner issuingKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issuingCertificate);
        ArgumentNullException.ThrowIfNull(issuingKey);
        DeviceId.Validate(deviceId);

        RequireSigningCa(issuingCertificate, issuingKey, now);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(deviceId);

        var certificateRequest = new CertificateRequest(subject.Build(), request.PublicKey, HashAlgorithmName.SHA256);

        certificateRequest.CertificateExtensions.Add(X509BasicConstraintsExtension.CreateForEndEntity(critical: true));

        // ECDSA in TLS: the device signs the handshake. No keyEncipherment, which ECDSA cannot do.
        certificateRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        // A client certificate, and nothing else: it cannot pass as a TLS server or sign code.
        certificateRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ClientAuthenticationOid)], critical: false));
        certificateRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(certificateRequest.PublicKey, critical: false));
        certificateRequest.CertificateExtensions.Add(AuthorityKeyIdentifier(issuingCertificate));

        return certificateRequest.Create(
            issuingCertificate.SubjectName,
            new SignerSignatureGenerator(issuingKey),
            now - ClockSkewAllowance,
            Earlier(now.AddYears(DeviceValidityYears), NotAfter(issuingCertificate)),
            SerialNumber.Create());
    }

    /// <summary>
    /// Verifies that <paramref name="certificate"/> chains to <paramref name="rootCertificate"/>, as a
    /// relying party would: signatures, validity, basic constraints, key usage and, for a device,
    /// the client authentication purpose. Revocation is not checked; IoT Hub does not check it either.
    /// </summary>
    /// <param name="certificate">The certificate to verify.</param>
    /// <param name="rootCertificate">The only trusted root.</param>
    /// <param name="issuingCertificate">The issuing CA, for a device certificate; otherwise null.</param>
    /// <param name="at">The time at which the chain must be valid.</param>
    /// <exception cref="CryptographicException">The chain does not verify.</exception>
    public static void VerifyChain(
        X509Certificate2 certificate,
        X509Certificate2 rootCertificate,
        X509Certificate2? issuingCertificate,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(rootCertificate);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = at.UtcDateTime;

        if (issuingCertificate is not null)
        {
            chain.ChainPolicy.ExtraStore.Add(issuingCertificate);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
        }

        var expectedLength = issuingCertificate is null ? 2 : 3;
        if (!chain.Build(certificate) || chain.ChainElements.Count != expectedLength)
        {
            var reasons = chain.ChainStatus.Select(status => status.StatusInformation.Trim()).Where(text => text.Length > 0);
            throw new CryptographicException(
                $"The certificate for '{certificate.Subject}' does not chain to the root CA: " +
                $"{string.Join("; ", reasons.DefaultIfEmpty($"{chain.ChainElements.Count} element(s), expected {expectedLength}"))}.");
        }
    }

    /// <summary>Whether <paramref name="signer"/> holds the key <paramref name="certificate"/> certifies.</summary>
    public static bool IsKeyOf(X509Certificate2 certificate, ICertificateSigner signer)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(signer);

        using var signerKey = ECDsa.Create(signer.PublicKey);
        return certificate.PublicKey.ExportSubjectPublicKeyInfo()
            .AsSpan()
            .SequenceEqual(signerKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The end of a certificate's validity, as an instant.</summary>
    public static DateTimeOffset NotAfter(X509Certificate2 certificate) => new(certificate.NotAfter);

    /// <summary>The start of a certificate's validity, as an instant.</summary>
    public static DateTimeOffset NotBefore(X509Certificate2 certificate) => new(certificate.NotBefore);

    private static X509KeyUsageExtension CaKeyUsage() =>
        // keyCertSign to sign certificates, cRLSign so a revocation list remains possible. IoT Hub
        // does not check one (ADR-009); the bit keeps the option open without changing the CA.
        new(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true);

    private static X509AuthorityKeyIdentifierExtension AuthorityKeyIdentifier(X509Certificate2 issuer)
    {
        var subjectKeyIdentifier = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"The CA certificate '{issuer.Subject}' has no subject key identifier to refer to.");

        return X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(subjectKeyIdentifier);
    }

    private static void RequireSigningCa(X509Certificate2 caCertificate, ICertificateSigner caKey, DateTimeOffset now)
    {
        if (!IsKeyOf(caCertificate, caKey))
        {
            throw new InvalidOperationException(
                $"The key '{caKey.Name}' does not belong to the CA certificate '{caCertificate.Subject}'. " +
                "Certificates it signed would not verify.");
        }

        if (now < NotBefore(caCertificate) || now >= NotAfter(caCertificate))
        {
            throw new InvalidOperationException(
                $"The CA certificate '{caCertificate.Subject}' is valid from {NotBefore(caCertificate):u} " +
                $"to {NotAfter(caCertificate):u}, not now.");
        }
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;
}
