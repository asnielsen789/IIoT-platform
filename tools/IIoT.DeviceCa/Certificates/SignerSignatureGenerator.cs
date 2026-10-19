using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IIoT.DeviceCa.Certificates;

/// <summary>
/// Lets <see cref="CertificateRequest"/> sign with a key it cannot hold: the CA key in Key Vault, or
/// any other <see cref="ICertificateSigner"/>.
/// </summary>
/// <remarks>
/// <para>
/// .NET builds the to-be-signed certificate and asks the generator for three things: the signature
/// algorithm identifier, the issuer's public key, and a signature over the bytes. The first two come
/// from the public key alone, so they are delegated to .NET's own ECDSA generator over a public-only
/// key. Only the signature goes to the signer.
/// </para>
/// <para>
/// Every signature is checked against the public key before it is returned. A signer bound to the
/// wrong key, or a conversion fault, then fails here, at issuance, rather than as a certificate that
/// IoT Hub rejects at connection time.
/// </para>
/// </remarks>
public sealed class SignerSignatureGenerator : X509SignatureGenerator
{
    private readonly ICertificateSigner _signer;
    private readonly ECDsa _publicKey;
    private readonly X509SignatureGenerator _publicKeyGenerator;

    public SignerSignatureGenerator(ICertificateSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        _signer = signer;
        _publicKey = ECDsa.Create(signer.PublicKey);
        _publicKeyGenerator = CreateForECDsa(_publicKey);
    }

    /// <summary>Only SHA-256: the hash Key Vault's ES256 is defined over.</summary>
    public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm)
    {
        RequireSha256(hashAlgorithm);
        return _publicKeyGenerator.GetSignatureAlgorithmIdentifier(hashAlgorithm);
    }

    public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm)
    {
        ArgumentNullException.ThrowIfNull(data);
        RequireSha256(hashAlgorithm);

        var digest = SHA256.HashData(data);
        var p1363 = _signer.SignDigest(digest);

        if (!_publicKey.VerifyHash(digest, p1363, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException(
                $"The signature from '{_signer.Name}' does not verify with its public key.");
        }

        return EcdsaSignature.P1363ToDer(p1363);
    }

    protected override PublicKey BuildPublicKey() => _publicKeyGenerator.PublicKey;

    private static void RequireSha256(HashAlgorithmName hashAlgorithm)
    {
        if (hashAlgorithm != HashAlgorithmName.SHA256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hashAlgorithm), hashAlgorithm, "The CA signs with ES256 (ECDSA P-256, SHA-256) only.");
        }
    }
}
