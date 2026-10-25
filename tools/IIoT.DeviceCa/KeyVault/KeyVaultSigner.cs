using System.Security.Cryptography;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.KeyVault;

/// <summary>
/// Signs with a CA key held in Key Vault. The private key never leaves the vault: only a SHA-256
/// digest goes in and a signature comes out.
/// </summary>
/// <remarks>
/// <para>
/// Bound to one key <em>version</em>, the one whose public key it reports, so the public key and
/// the signatures always belong together even if someone adds a version in between. The Bicep
/// template never adds one (see <c>infra/modules/device-ca-keys.bicep</c>).
/// </para>
/// <para>
/// Needs <c>keys/read</c> to fetch the public key and <c>keys/sign/action</c> to sign: both are in
/// the built-in role Key Vault Crypto User.
/// </para>
/// </remarks>
public sealed class KeyVaultSigner : ICertificateSigner
{
    private readonly CryptographyClient _cryptography;

    private KeyVaultSigner(string name, ECParameters publicKey, CryptographyClient cryptography)
    {
        Name = name;
        PublicKey = publicKey;
        _cryptography = cryptography;
    }

    public string Name { get; }

    public ECParameters PublicKey { get; }

    /// <summary>Fetches the current version of <paramref name="keyName"/> and binds a signer to it.</summary>
    /// <exception cref="InvalidOperationException">The key is not an EC P-256 key that may sign.</exception>
    public static async Task<KeyVaultSigner> CreateAsync(
        KeyClient keys,
        TokenCredential credential,
        string keyName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        KeyVaultKey key = await keys.GetKeyAsync(keyName, cancellationToken: cancellationToken);
        var jwk = key.Key;

        if (jwk.KeyType != KeyType.Ec && jwk.KeyType != KeyType.EcHsm)
            throw new InvalidOperationException($"Key '{keyName}' is {jwk.KeyType}, not an EC key.");

        if (jwk.CurveName != KeyCurveName.P256)
            throw new InvalidOperationException($"Key '{keyName}' is on curve {jwk.CurveName}, not P-256.");

        if (!key.KeyOperations.Contains(KeyOperation.Sign))
            throw new InvalidOperationException($"Key '{keyName}' does not permit signing.");

        var publicKey = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = jwk.X, Y = jwk.Y }
        };

        // key.Id carries the version, which pins the signer to the public key read above.
        return new KeyVaultSigner(keyName, publicKey, new CryptographyClient(key.Id, credential));
    }

    /// <summary>Signs with ES256 in the vault. Key Vault returns the JWS form, r || s.</summary>
    public byte[] SignDigest(byte[] sha256Digest)
    {
        ArgumentNullException.ThrowIfNull(sha256Digest);

        // Synchronous because X509SignatureGenerator.SignData is: CertificateRequest.Create signs
        // inline. The tool signs one certificate at a time, so nothing waits on this but the user.
        SignResult result = _cryptography.Sign(SignatureAlgorithm.ES256, sha256Digest);
        return result.Signature;
    }
}
