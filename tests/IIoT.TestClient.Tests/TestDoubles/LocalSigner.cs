using System.Security.Cryptography;
using IIoT.DeviceCa.Certificates;

namespace IIoT.TestClient.Tests.TestDoubles;

/// <summary>
/// A local P-256 key standing in for a Key Vault CA key. It returns IEEE P1363 signatures, as Key
/// Vault's ES256 does, so the test CA signs through the same conversion as the real one.
/// </summary>
public sealed class LocalSigner : ICertificateSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public LocalSigner(string name) => Name = name;

    public string Name { get; }

    public ECParameters PublicKey => _key.ExportParameters(includePrivateParameters: false);

    public byte[] SignDigest(byte[] sha256Digest) =>
        _key.SignHash(sha256Digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
