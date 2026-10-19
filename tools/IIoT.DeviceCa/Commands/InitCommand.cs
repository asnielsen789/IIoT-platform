using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.KeyVault;

namespace IIoT.DeviceCa.Commands;

/// <summary>
/// <c>init</c>: builds the root and issuing CA certificates from the two CA keys and stores them in
/// the vault. Run once per CA.
/// </summary>
public sealed class InitCommand
{
    private readonly ICaVault _vault;
    private readonly TimeProvider _time;
    private readonly TextWriter _output;

    public InitCommand(ICaVault vault, TimeProvider time, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(output);

        _vault = vault;
        _time = time;
        _output = output;
    }

    /// <summary>Creates and stores the CA certificates.</summary>
    /// <param name="force">Replace CA certificates that exist already.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="CaRefusedException">
    /// The vault holds CA certificates already and <paramref name="force"/> is false. Nothing was
    /// changed.
    /// </exception>
    public async Task RunAsync(bool force, CancellationToken cancellationToken)
    {
        var existing = new List<string>();
        foreach (var name in new[] { CaNames.RootCertificate, CaNames.IssuingCertificate })
        {
            if (await _vault.GetCertificatePemAsync(name, cancellationToken) is not null)
                existing.Add(name);
        }

        if (existing.Count > 0 && !force)
        {
            // The certificates are what every issued device chains to, and what IoT Hub has
            // registered. Replacing them is never a side effect of running init twice.
            throw new CaRefusedException(
                $"{_vault.Name} holds CA certificates already ({string.Join(", ", existing)}); nothing was " +
                "changed. Replacing them can orphan every device issued so far: their chain files hold the " +
                "old issuing CA certificate, and IoT Hub has the old one registered. Pass --force only to " +
                "start the CA again, for example after its keys were recreated, and then register the new " +
                "issuing CA certificate and issue every device a new certificate.");
        }

        var rootKey = await _vault.GetSignerAsync(CaNames.RootKey, cancellationToken);
        var issuingKey = await _vault.GetSignerAsync(CaNames.IssuingKey, cancellationToken);

        var now = _time.GetUtcNow();
        using var root = CertificateAuthority.CreateRoot(rootKey, now);
        using var issuing = CertificateAuthority.CreateIssuing(issuingKey, root, rootKey, now);

        // A malformed CA fails only when the first device connects (ADR-009). Check it as a relying
        // party would before anything is stored.
        CertificateAuthority.VerifyChain(issuing, root, issuingCertificate: null, now);

        // Root first: an issuing certificate is only useful next to the root that signed it, and a
        // run that stops in between is repaired by running again with --force.
        await _vault.SetCertificatePemAsync(CaNames.RootCertificate, root.ExportCertificatePem(), cancellationToken);
        await _vault.SetCertificatePemAsync(CaNames.IssuingCertificate, issuing.ExportCertificatePem(), cancellationToken);

        await _output.WriteLineAsync($"Stored the CA certificates in {_vault.Name}:");
        await Describe(CaNames.RootCertificate, root);
        await Describe(CaNames.IssuingCertificate, issuing);
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(
            "Next: export the issuing CA certificate with 'ca-certificate' and register it in IoT Hub " +
            "as verified (infra/README.md, \"Device certificate authority (one-time)\").");
    }

    private async Task Describe(string name, X509Certificate2 certificate)
    {
        await _output.WriteLineAsync($"  {name}: {certificate.Subject}");
        await _output.WriteLineAsync(
            $"    valid {CertificateAuthority.NotBefore(certificate):u} to {CertificateAuthority.NotAfter(certificate):u}, " +
            $"SHA-256 thumbprint {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
    }
}
