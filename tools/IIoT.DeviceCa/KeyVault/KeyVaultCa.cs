using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;
using IIoT.DeviceCa.Certificates;

namespace IIoT.DeviceCa.KeyVault;

/// <summary>The CA in a Key Vault: keys for signing, secrets for the certificates.</summary>
/// <remarks>
/// <para>
/// The certificates are public data. They are secrets only because a secret is the vault's plain
/// "store a document" primitive; a Key Vault <em>certificate</em> object would bind each to a key
/// the vault generates for it, which is not how this CA's keys are managed. Reading and writing
/// them needs the built-in role Key Vault Secrets Officer.
/// </para>
/// </remarks>
public sealed class KeyVaultCa : ICaVault
{
    private readonly KeyClient _keys;
    private readonly SecretClient _secrets;
    private readonly TokenCredential _credential;

    public KeyVaultCa(Uri vaultUri, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        ArgumentNullException.ThrowIfNull(credential);

        Name = vaultUri.Host;
        _credential = credential;
        _keys = new KeyClient(vaultUri, credential);
        _secrets = new SecretClient(vaultUri, credential);
    }

    public string Name { get; }

    /// <summary>
    /// Turns <c>--vault</c> into a vault URI: a bare name gains <c>https://</c> and
    /// <c>.vault.azure.net</c>; anything containing <c>://</c> is used verbatim, for another cloud.
    /// </summary>
    public static Uri ResolveUri(string nameOrUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrUri);

        var value = nameOrUri.Trim();
        return value.Contains("://", StringComparison.Ordinal)
            ? new Uri(value)
            : new Uri($"https://{value}.vault.azure.net/");
    }

    public async Task<ICertificateSigner> GetSignerAsync(string keyName, CancellationToken cancellationToken) =>
        await KeyVaultSigner.CreateAsync(_keys, _credential, keyName, cancellationToken);

    public async Task<string?> GetCertificatePemAsync(string secretName, CancellationToken cancellationToken)
    {
        try
        {
            KeyVaultSecret secret = await _secrets.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            return secret.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SetCertificatePemAsync(string secretName, string pem, CancellationToken cancellationToken)
    {
        var secret = new KeyVaultSecret(secretName, pem)
        {
            Properties = { ContentType = CaNames.PemContentType }
        };

        try
        {
            await _secrets.SetSecretAsync(secret, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Soft delete keeps a deleted secret's name reserved until it is purged or recovered.
            throw new InvalidOperationException(
                $"The vault holds a deleted secret named '{secretName}'. Recover it " +
                $"(az keyvault secret recover) or purge it (az keyvault secret purge), then run again.", ex);
        }
    }
}
