using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Commands;
using IIoT.DeviceCa.KeyVault;
using IIoT.DeviceCa.Tests.TestDoubles;

namespace IIoT.DeviceCa.Tests.Commands;

public sealed class InitCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeCaVault _vault = new();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task Run_StoresBothCaCertificates_AsAChainFromTheVaultKeys()
    {
        await Init();

        using var root = Certificate(CaNames.RootCertificate);
        using var issuing = Certificate(CaNames.IssuingCertificate);

        Assert.Equal(CertificateAuthority.RootSubject, root.Subject);
        Assert.Equal(CertificateAuthority.IssuingSubject, issuing.Subject);
        Assert.True(CertificateAuthority.IsKeyOf(root, _vault.RootKey));
        Assert.True(CertificateAuthority.IsKeyOf(issuing, _vault.IssuingKey));
        CertificateAuthority.VerifyChain(issuing, root, issuingCertificate: null, Now);

        // Public certificates only: no private key material in what the vault stores.
        Assert.All(_vault.Secrets.Values, pem => Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_Twice_RefusesAndChangesNothing()
    {
        await Init();
        var before = new Dictionary<string, string>(_vault.Secrets);
        var writes = _vault.SecretWrites;
        var signatures = _vault.RootKey.Signatures;

        var exception = await Assert.ThrowsAsync<CaRefusedException>(() => Init());

        // Replacing the CA certificates can orphan every device already issued.
        Assert.Contains("orphan", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--force", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, _vault.Secrets);
        Assert.Equal(writes, _vault.SecretWrites);
        Assert.Equal(signatures, _vault.RootKey.Signatures);
    }

    [Fact]
    public async Task Run_WithOnlyOneCertificatePresent_StillRefuses()
    {
        _vault.Secrets[CaNames.IssuingCertificate] = "left over from an interrupted run";

        await Assert.ThrowsAsync<CaRefusedException>(() => Init());

        Assert.Equal(0, _vault.SecretWrites);
    }

    [Fact]
    public async Task Run_WithForce_ReplacesTheCertificates()
    {
        await Init();
        var firstIssuing = _vault.Secrets[CaNames.IssuingCertificate];

        await Init(force: true);

        Assert.NotEqual(firstIssuing, _vault.Secrets[CaNames.IssuingCertificate]);
        Assert.Equal(4, _vault.SecretWrites);
    }

    [Fact]
    public async Task Run_PointsAtTheNextStep()
    {
        var output = new StringWriter();

        await new InitCommand(_vault, new FixedTime(Now), output).RunAsync(force: false, CancellationToken.None);

        Assert.Contains("ca-certificate", output.ToString(), StringComparison.Ordinal);
    }

    private Task Init(bool force = false) =>
        new InitCommand(_vault, new FixedTime(Now), TextWriter.Null).RunAsync(force, CancellationToken.None);

    private X509Certificate2 Certificate(string secretName) =>
        X509Certificate2.CreateFromPem(_vault.Secrets[secretName]);
}
