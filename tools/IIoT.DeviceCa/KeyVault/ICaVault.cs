using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.KeyVault;

/// <summary>
/// Where the CA lives: its two signing keys and its two certificates.
/// </summary>
/// <remarks>
/// Implemented over Key Vault by <see cref="KeyVaultCa"/>. The commands depend on this interface
/// only, so their rules (never overwrite without <c>--force</c>, never sign with a key that does
/// not match its certificate) are tested without a vault.
/// </remarks>
public interface ICaVault
{
    /// <summary>A display name for messages, such as the vault's name.</summary>
    string Name { get; }

    /// <summary>Returns a signer for the CA key <paramref name="keyName"/>.</summary>
    Task<ICertificateSigner> GetSignerAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a CA certificate (PEM), or returns <see langword="null"/> if the vault holds none by that
    /// name.
    /// </summary>
    Task<string?> GetCertificatePemAsync(string secretName, CancellationToken cancellationToken);

    /// <summary>Stores a CA certificate (PEM), replacing any earlier one.</summary>
    Task SetCertificatePemAsync(string secretName, string pem, CancellationToken cancellationToken);
}
