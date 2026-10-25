using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Tests.TestDoubles;

namespace IIoT.DeviceCa.Tests.Certificates;

/// <summary>
/// The certificate profiles of ADR-009, built with local keys that sign the way Key Vault does. A
/// malformed certificate fails only when a device connects, so every extension is asserted here.
/// </summary>
public sealed class CertificateAuthorityTests : IDisposable
{
    private const string DeviceId = "tank-000002";
    private const string EcdsaWithSha256Oid = "1.2.840.10045.4.3.2";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly TestCa _ca = new(Now);
    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _ca.Dispose();
        _deviceKey.Dispose();
    }

    [Fact]
    public void TheChain_VerifiesFromDeviceToRoot()
    {
        using var device = IssueDevice();

        // Independently of CertificateAuthority.VerifyChain: the root as the only trust anchor, the
        // issuing CA as the only extra certificate, nothing from the machine's stores.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca.Root);
        chain.ChainPolicy.ExtraStore.Add(_ca.Issuing);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CertificateAuthority.ClientAuthenticationOid));

        Assert.True(chain.Build(device), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        Assert.Equal(
            [device.Thumbprint, _ca.Issuing.Thumbprint, _ca.Root.Thumbprint],
            chain.ChainElements.Select(element => element.Certificate.Thumbprint));

        CertificateAuthority.VerifyChain(device, _ca.Root, _ca.Issuing, Now);
    }

    [Fact]
    public void Root_IsASelfSignedCaForCertificatesOnly()
    {
        var root = _ca.Root;

        Assert.Equal(CertificateAuthority.RootSubject, root.Subject);
        Assert.Equal(root.Subject, root.Issuer);
        Assert.Equal(EcdsaWithSha256Oid, root.SignatureAlgorithm.Value);

        var constraints = Extension<X509BasicConstraintsExtension>(root);
        Assert.True(constraints.Critical);
        Assert.True(constraints.CertificateAuthority);
        Assert.False(constraints.HasPathLengthConstraint);

        AssertCaKeyUsage(root);
        Assert.NotNull(Extension<X509SubjectKeyIdentifierExtension>(root).SubjectKeyIdentifier);
        Assert.Empty(root.Extensions.OfType<X509EnhancedKeyUsageExtension>());

        Assert.Equal(Now - CertificateAuthority.ClockSkewAllowance, CertificateAuthority.NotBefore(root));
        Assert.Equal(Now.AddYears(20), CertificateAuthority.NotAfter(root));
        Assert.True(CertificateAuthority.IsKeyOf(root, _ca.RootKey));
    }

    [Fact]
    public void Issuing_IsACaThatMaySignDevicesOnly()
    {
        var issuing = _ca.Issuing;

        Assert.Equal(CertificateAuthority.IssuingSubject, issuing.Subject);
        Assert.Equal(_ca.Root.Subject, issuing.Issuer);
        Assert.Equal(EcdsaWithSha256Oid, issuing.SignatureAlgorithm.Value);

        var constraints = Extension<X509BasicConstraintsExtension>(issuing);
        Assert.True(constraints.Critical);
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);

        AssertCaKeyUsage(issuing);
        Assert.NotNull(Extension<X509SubjectKeyIdentifierExtension>(issuing).SubjectKeyIdentifier);
        AssertAuthorityKeyIdentifier(issuing, _ca.Root);

        Assert.Equal(Now.AddYears(10), CertificateAuthority.NotAfter(issuing));
        Assert.True(CertificateAuthority.IsKeyOf(issuing, _ca.IssuingKey));
        CertificateAuthority.VerifyChain(issuing, _ca.Root, issuingCertificate: null, Now);
    }

    [Fact]
    public void Device_IsAClientCertificateForTheCsrKey()
    {
        using var device = IssueDevice();

        Assert.Equal(_ca.Issuing.Subject, device.Issuer);
        Assert.Equal(EcdsaWithSha256Oid, device.SignatureAlgorithm.Value);
        Assert.False(device.HasPrivateKey);

        // The CSR's key, and no other.
        using var certifiedKey = device.GetECDsaPublicKey()!;
        Assert.Equal(_deviceKey.ExportSubjectPublicKeyInfo(), certifiedKey.ExportSubjectPublicKeyInfo());

        var constraints = Extension<X509BasicConstraintsExtension>(device);
        Assert.True(constraints.Critical);
        Assert.False(constraints.CertificateAuthority);

        var keyUsage = Extension<X509KeyUsageExtension>(device);
        Assert.True(keyUsage.Critical);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);

        var extendedKeyUsage = Extension<X509EnhancedKeyUsageExtension>(device);
        Assert.Equal(
            [CertificateAuthority.ClientAuthenticationOid],
            extendedKeyUsage.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value));

        Assert.NotNull(Extension<X509SubjectKeyIdentifierExtension>(device).SubjectKeyIdentifier);
        AssertAuthorityKeyIdentifier(device, _ca.Issuing);

        // Nothing beyond the profile, whatever the CSR carried.
        Assert.Equal(5, device.Extensions.Count);
    }

    [Fact]
    public void Device_SubjectIsTheDeviceId_NotWhatTheCsrAskedFor()
    {
        using var device = IssueDevice(csrSubject: "CN=someone-else, O=Not Trusted");

        Assert.Equal($"CN={DeviceId}", device.Subject);
        Assert.Equal(DeviceId, device.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Single(device.SubjectName.EnumerateRelativeDistinguishedNames());
    }

    [Fact]
    public void Device_SpecialCharactersInTheId_StayOneCommonName()
    {
        // ',' and '=' are separators in a DN's text form. Built structurally, they stay inside the
        // one CN value instead of starting new attributes.
        const string id = "a.b_c-d%e*f?g!h(i)j,k:l=m@n$o'p";

        using var device = IssueDevice(deviceId: id);

        Assert.Equal(id, device.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Single(device.SubjectName.EnumerateRelativeDistinguishedNames());
    }

    [Fact]
    public void Device_IsValidForTwoYears_FromFiveMinutesAgo()
    {
        using var device = IssueDevice();

        Assert.Equal(Now - TimeSpan.FromMinutes(5), CertificateAuthority.NotBefore(device));
        Assert.Equal(Now.AddYears(2), CertificateAuthority.NotAfter(device));
    }

    [Fact]
    public void Device_NeverOutlivesTheIssuingCa()
    {
        // An issuing CA created nine and a half years ago has half a year left.
        var created = Now.AddYears(-10).AddMonths(6);
        using var rootKey = new LocalSigner();
        using var issuingKey = new LocalSigner();
        using var root = CertificateAuthority.CreateRoot(rootKey, created);
        using var issuing = CertificateAuthority.CreateIssuing(issuingKey, root, rootKey, created);

        using var device = CertificateAuthority.IssueDevice(Request(), DeviceId, issuing, issuingKey, Now);

        Assert.Equal(CertificateAuthority.NotAfter(issuing), CertificateAuthority.NotAfter(device));
        Assert.True(CertificateAuthority.NotAfter(device) < Now.AddYears(1));
        CertificateAuthority.VerifyChain(device, root, issuing, Now);
    }

    [Fact]
    public void Issuing_NeverOutlivesTheRoot()
    {
        var created = Now.AddYears(-15);
        using var rootKey = new LocalSigner();
        using var issuingKey = new LocalSigner();
        using var root = CertificateAuthority.CreateRoot(rootKey, created);

        using var issuing = CertificateAuthority.CreateIssuing(issuingKey, root, rootKey, Now);

        Assert.Equal(CertificateAuthority.NotAfter(root), CertificateAuthority.NotAfter(issuing));
    }

    [Fact]
    public void SerialNumbers_ArePositiveRandomAndSixteenBytes()
    {
        using var first = IssueDevice();
        using var second = IssueDevice();

        foreach (var certificate in new[] { _ca.Root, _ca.Issuing, first, second })
        {
            var serial = certificate.SerialNumberBytes.Span;
            Assert.Equal(SerialNumber.Length, serial.Length);

            // Big-endian as encoded: a set high bit would make the integer negative.
            Assert.InRange(serial[0], 0x40, 0x7F);
        }

        Assert.NotEqual(first.SerialNumber, second.SerialNumber);
    }

    [Fact]
    public void IssueDevice_AnInvalidDeviceId_IsRejectedBeforeSigning()
    {
        var signaturesBefore = _ca.IssuingKey.Signatures;

        Assert.Throws<ArgumentException>(() => IssueDevice(deviceId: "tank#2"));
        Assert.Equal(signaturesBefore, _ca.IssuingKey.Signatures);
    }

    [Fact]
    public void IssueDevice_WithAKeyThatIsNotTheIssuingCas_IsRejected()
    {
        // A vault key replaced after init, say: its signatures would not verify against the
        // registered issuing certificate, so nothing is signed with it.
        using var otherKey = new LocalSigner("another-key");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CertificateAuthority.IssueDevice(Request(), DeviceId, _ca.Issuing, otherKey, Now));

        Assert.Contains("another-key", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, otherKey.Signatures);
    }

    [Fact]
    public void IssueDevice_AfterTheIssuingCaExpired_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CertificateAuthority.IssueDevice(
            Request(), DeviceId, _ca.Issuing, _ca.IssuingKey, Now.AddYears(11)));
    }

    [Fact]
    public void VerifyChain_ADeviceFromAnotherCa_Fails()
    {
        using var other = new TestCa(Now);
        using var foreign = CertificateAuthority.IssueDevice(Request(), DeviceId, other.Issuing, other.IssuingKey, Now);

        Assert.Throws<CryptographicException>(() => CertificateAuthority.VerifyChain(foreign, _ca.Root, _ca.Issuing, Now));
    }

    [Fact]
    public void Signing_WithASignerThatLies_FailsAtIssuance()
    {
        // A signer whose signatures do not match its public key: caught before a certificate exists.
        using var liar = new MismatchedSigner();

        Assert.Throws<CryptographicException>(() => CertificateAuthority.CreateRoot(liar, Now));
    }

    private X509Certificate2 IssueDevice(string deviceId = DeviceId, string csrSubject = "CN=requested-by-device") =>
        CertificateAuthority.IssueDevice(Request(csrSubject), deviceId, _ca.Issuing, _ca.IssuingKey, Now);

    private DeviceCertificateRequest Request(string subject = "CN=requested-by-device") =>
        DeviceCertificateRequest.LoadPem(Csr.Create(_deviceKey, subject));

    private static T Extension<T>(X509Certificate2 certificate) where T : X509Extension =>
        Assert.Single(certificate.Extensions.OfType<T>());

    private static void AssertCaKeyUsage(X509Certificate2 certificate)
    {
        var keyUsage = Extension<X509KeyUsageExtension>(certificate);
        Assert.True(keyUsage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, keyUsage.KeyUsages);
    }

    private static void AssertAuthorityKeyIdentifier(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var authorityKeyIdentifier = Extension<X509AuthorityKeyIdentifierExtension>(certificate);
        var issuerKeyIdentifier = Extension<X509SubjectKeyIdentifierExtension>(issuer);

        Assert.False(authorityKeyIdentifier.Critical);
        Assert.Equal(
            Convert.FromHexString(issuerKeyIdentifier.SubjectKeyIdentifier!),
            authorityKeyIdentifier.KeyIdentifier!.Value.ToArray());
    }

    /// <summary>Reports one key's public half and signs with another.</summary>
    private sealed class MismatchedSigner : ICertificateSigner, IDisposable
    {
        private readonly LocalSigner _reported = new();
        private readonly LocalSigner _signing = new();

        public string Name => "mismatched";

        public ECParameters PublicKey => _reported.PublicKey;

        public byte[] SignDigest(byte[] sha256Digest) => _signing.SignDigest(sha256Digest);

        public void Dispose()
        {
            _reported.Dispose();
            _signing.Dispose();
        }
    }
}
