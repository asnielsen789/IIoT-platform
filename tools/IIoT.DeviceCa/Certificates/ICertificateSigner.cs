using System.Security.Cryptography;

namespace IIoT.DeviceCa.Certificates;

/// <summary>
/// A CA's EC P-256 signing key, wherever it is held.
/// </summary>
/// <remarks>
/// <para>
/// In operation this is a Key Vault key that never leaves the vault (ADR-009); in the tests it is
/// a local key. Certificate building depends only on this interface, so the certificate profiles
/// are tested without Azure.
/// </para>
/// <para>
/// The contract follows Key Vault's ES256 operation: the caller hashes, the signer signs the
/// SHA-256 digest and returns the signature in IEEE P1363 form, <c>r || s</c>, 64 bytes. X.509
/// needs DER instead; <see cref="EcdsaSignature"/> converts. A local test signer returns P1363
/// too, so the tests run the same conversion as Key Vault signing does.
/// </para>
/// </remarks>
public interface ICertificateSigner
{
    /// <summary>A name for messages, such as the Key Vault key name.</summary>
    string Name { get; }

    /// <summary>The public half of the key: curve P-256 and the point Q.</summary>
    ECParameters PublicKey { get; }

    /// <summary>Signs a SHA-256 digest.</summary>
    /// <param name="sha256Digest">The 32-byte digest of the data to sign.</param>
    /// <returns>The signature as IEEE P1363, <c>r || s</c>, 32 bytes each.</returns>
    byte[] SignDigest(byte[] sha256Digest);
}
