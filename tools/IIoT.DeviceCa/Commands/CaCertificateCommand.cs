using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.KeyVault;

namespace IIoT.DeviceCa.Commands;

/// <summary>
/// <c>ca-certificate</c>: writes the issuing CA certificate, the one IoT Hub authenticates devices
/// against, so it can be registered with <c>az iot hub certificate create</c>.
/// </summary>
public sealed class CaCertificateCommand
{
    private readonly ICaVault _vault;
    private readonly TextWriter _output;

    public CaCertificateCommand(ICaVault vault, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(output);

        _vault = vault;
        _output = output;
    }

    /// <summary>Writes the certificate to <paramref name="outputFile"/>, or to the output when null.</summary>
    /// <exception cref="CaRefusedException">The vault holds no issuing CA certificate.</exception>
    public async Task RunAsync(string? outputFile, CancellationToken cancellationToken)
    {
        var pem = await _vault.GetCertificatePemAsync(CaNames.IssuingCertificate, cancellationToken)
            ?? throw new CaRefusedException($"{_vault.Name} has no secret '{CaNames.IssuingCertificate}'. Run 'init' first.");

        // Parsed rather than passed through, so a damaged secret is reported here and not by IoT Hub.
        using var certificate = Pem.LoadCertificate(pem, $"Secret '{CaNames.IssuingCertificate}'");
        var exported = certificate.ExportCertificatePem() + "\n";

        if (outputFile is null)
        {
            // Nothing but the PEM, so the output can be redirected or piped.
            await _output.WriteAsync(exported);
            return;
        }

        // Overwriting is fine here: this is a copy of what the vault holds, not an issued artefact.
        await File.WriteAllTextAsync(outputFile, exported, cancellationToken);
        await _output.WriteLineAsync(
            $"Wrote {certificate.Subject} to {outputFile} (valid to {CertificateAuthority.NotAfter(certificate):u}, " +
            $"SHA-1 thumbprint {certificate.Thumbprint}, which IoT Hub shows for it).");
    }
}
