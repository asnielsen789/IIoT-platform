using System.Security.Cryptography;
using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.Tests.TestDoubles;

/// <summary>
/// A local P-256 key standing in for a Key Vault key. It returns IEEE P1363 signatures, exactly as
/// Key Vault's ES256 does, so every certificate a test builds goes through the same P1363-to-DER
/// conversion that signing in the vault does.
/// </summary>
public sealed class LocalSigner : ICertificateSigner, IDisposable
{
    private readonly ECDsa _key;

    public LocalSigner(string name = "local-test-key")
    {
        Name = name;
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public string Name { get; }

    public ECParameters PublicKey => _key.ExportParameters(includePrivateParameters: false);

    /// <summary>How many signatures this key has made.</summary>
    public int Signatures { get; private set; }

    public byte[] SignDigest(byte[] sha256Digest)
    {
        Signatures++;
        return _key.SignHash(sha256Digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose() => _key.Dispose();
}
