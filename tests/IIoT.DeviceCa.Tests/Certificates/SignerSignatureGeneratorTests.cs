using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Tests.TestDoubles;

namespace IIoT.DeviceCa.Tests.Certificates;

/// <summary>The adapter that lets <see cref="CertificateRequest"/> sign through a signer it cannot hold.</summary>
public class SignerSignatureGeneratorTests
{
    [Fact]
    public void SignData_ReturnsDer_ThatTheSignersPublicKeyVerifies()
    {
        using var signer = new LocalSigner();
        var generator = new SignerSignatureGenerator(signer);
        var data = "to be signed"u8.ToArray();

        var signature = generator.SignData(data, HashAlgorithmName.SHA256);

        using var publicKey = ECDsa.Create(signer.PublicKey);
        Assert.True(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal(1, signer.Signatures);
    }

    [Fact]
    public void PublicKey_IsTheSigners()
    {
        using var signer = new LocalSigner();
        using var expected = ECDsa.Create(signer.PublicKey);

        var generator = new SignerSignatureGenerator(signer);

        Assert.Equal(expected.ExportSubjectPublicKeyInfo(), generator.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void AnythingButSha256_IsRefused()
    {
        // ES256 is defined over SHA-256 only; another hash would make Key Vault reject the digest.
        using var signer = new LocalSigner();
        var generator = new SignerSignatureGenerator(signer);

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.SignData([1, 2, 3], HashAlgorithmName.SHA384));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.GetSignatureAlgorithmIdentifier(HashAlgorithmName.SHA1));
        Assert.Equal(0, signer.Signatures);
    }
}
