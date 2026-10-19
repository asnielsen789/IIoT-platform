using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.KeyVault;

namespace IIoT.DeviceCa.Tests.TestDoubles;

/// <summary>An in-memory vault with local CA keys under the real key names.</summary>
public sealed class FakeCaVault : ICaVault, IDisposable
{
    public LocalSigner RootKey { get; } = new(CaNames.RootKey);

    public LocalSigner IssuingKey { get; } = new(CaNames.IssuingKey);

    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public int SecretWrites { get; private set; }

    public string Name => "fake-vault";

    public Task<ICertificateSigner> GetSignerAsync(string keyName, CancellationToken cancellationToken) =>
        Task.FromResult<ICertificateSigner>(keyName switch
        {
            CaNames.RootKey => RootKey,
            CaNames.IssuingKey => IssuingKey,
            _ => throw new KeyNotFoundException(keyName)
        });

    public Task<string?> GetCertificatePemAsync(string secretName, CancellationToken cancellationToken) =>
        Task.FromResult(Secrets.GetValueOrDefault(secretName));

    public Task SetCertificatePemAsync(string secretName, string pem, CancellationToken cancellationToken)
    {
        Secrets[secretName] = pem;
        SecretWrites++;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        RootKey.Dispose();
        IssuingKey.Dispose();
    }
}
