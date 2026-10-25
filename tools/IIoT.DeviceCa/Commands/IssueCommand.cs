using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.IotHub;
using IIoT.DeviceCa.KeyVault;

namespace IIoT.DeviceCa.Commands;

/// <summary>
/// <c>issue</c>: issues a device certificate for the key in a CSR, writes the certificate and its
/// chain, and registers the device in IoT Hub as CA-authenticated.
/// </summary>
/// <remarks>
/// Checks run cheapest and least invasive first: the device ID, the CSR and the output files before
/// the vault or the hub is asked anything, and the registry before anything is signed. A refusal
/// therefore leaves no certificate behind and no device registered.
/// </remarks>
public sealed class IssueCommand
{
    private readonly ICaVault _vault;
    private readonly IDeviceRegistry _registry;
    private readonly TimeProvider _time;
    private readonly TextWriter _output;

    public IssueCommand(ICaVault vault, IDeviceRegistry registry, TimeProvider time, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(output);

        _vault = vault;
        _registry = registry;
        _time = time;
        _output = output;
    }

    /// <summary>Issues and registers.</summary>
    /// <param name="csrFile">The device's CSR, PEM.</param>
    /// <param name="deviceId">The IoT Hub device ID; the certificate's subject is <c>CN=&lt;deviceId&gt;</c>.</param>
    /// <param name="outputDirectory">Where the certificate and chain files go. Created if missing.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="CaRefusedException">
    /// The device ID, the CSR or the CA is unusable, an output file exists, or the device is
    /// registered with another authentication type. Nothing was changed.
    /// </exception>
    public async Task<IssuedCertificate> RunAsync(
        string csrFile, string deviceId, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csrFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (DeviceId.Problem(deviceId) is { } problem)
            throw new CaRefusedException($"{problem} Nothing was issued.");

        var request = await LoadRequestAsync(csrFile, cancellationToken);

        var certificatePath = Path.Combine(outputDirectory, $"{FileStem(deviceId)}.cert.pem");
        var chainPath = Path.Combine(outputDirectory, $"{FileStem(deviceId)}.chain.pem");
        foreach (var path in new[] { certificatePath, chainPath })
        {
            if (File.Exists(path))
            {
                throw new CaRefusedException(
                    $"{path} exists; nothing was issued. Move it away to issue this device a new certificate.");
            }
        }

        // Never change how an existing device authenticates. Moving a device from SAS to X.509 is a
        // decision about that device, not a side effect of issuing a certificate.
        var existing = await _registry.GetAsync(deviceId, cancellationToken);
        if (existing is { IsCertificateAuthority: false })
        {
            throw new CaRefusedException(
                $"Device '{deviceId}' is registered in {_registry.Name} with authentication type " +
                $"{existing.AuthenticationType}, not CertificateAuthority. Nothing was issued or changed. " +
                "Use another device ID, or delete the identity first if it is really meant to move to a certificate.");
        }

        var now = _time.GetUtcNow();
        using var root = await LoadCaCertificateAsync(CaNames.RootCertificate, cancellationToken);
        using var issuing = await LoadCaCertificateAsync(CaNames.IssuingCertificate, cancellationToken);
        CertificateAuthority.VerifyChain(issuing, root, issuingCertificate: null, now);

        var issuingKey = await _vault.GetSignerAsync(CaNames.IssuingKey, cancellationToken);
        using var certificate = CertificateAuthority.IssueDevice(request, deviceId, issuing, issuingKey, now);

        // As IoT Hub will check it: signature, validity, constraints and the client-auth purpose.
        CertificateAuthority.VerifyChain(certificate, root, issuing, now);

        Directory.CreateDirectory(outputDirectory);
        var certificatePem = certificate.ExportCertificatePem() + "\n";

        // The chain stops at the issuing CA. That is the certificate IoT Hub has registered, so the
        // root adds nothing to the handshake but bytes, and every byte crosses NB-IoT on every
        // connection.
        Pem.WriteNewFile(certificatePath, certificatePem);
        Pem.WriteNewFile(chainPath, certificatePem + issuing.ExportCertificatePem() + "\n");

        var registered = existing is null && await RegisterAsync(deviceId, cancellationToken);

        var subjectNote = request.RequestedSubject.Name == certificate.Subject
            ? string.Empty
            : $" (the CSR asked for '{request.RequestedSubject.Name}', which is ignored)";

        await _output.WriteLineAsync($"Issued {certificate.Subject}{subjectNote}");
        await _output.WriteLineAsync(
            $"  serial {certificate.SerialNumber}, valid {CertificateAuthority.NotBefore(certificate):u} to " +
            $"{CertificateAuthority.NotAfter(certificate):u}");
        await _output.WriteLineAsync($"  {certificatePath}");
        await _output.WriteLineAsync($"  {chainPath} (device and issuing CA certificate: give this one to the device)");
        await _output.WriteLineAsync(registered
            ? $"Registered '{deviceId}' in {_registry.Name}, authenticated by the issuing CA."
            : $"'{deviceId}' was already registered in {_registry.Name} as CA-authenticated; left as it is.");

        return new IssuedCertificate(
            certificatePath, chainPath, certificate.SerialNumber, CertificateAuthority.NotAfter(certificate), registered);
    }

    /// <summary>
    /// The device ID as a file name. Device IDs may contain characters that some file systems refuse
    /// or misread, such as <c>:</c> on Windows, so anything but letters, digits, '.', '_' and '-'
    /// becomes '_'. The test client names its key and CSR the same way.
    /// </summary>
    public static string FileStem(string deviceId) =>
        string.Concat(deviceId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));

    private static async Task<DeviceCertificateRequest> LoadRequestAsync(string csrFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(csrFile))
            throw new CaRefusedException($"CSR file not found: {csrFile}");

        try
        {
            return DeviceCertificateRequest.LoadPem(await File.ReadAllTextAsync(csrFile, cancellationToken));
        }
        catch (InvalidDataException ex)
        {
            throw new CaRefusedException($"{csrFile}: {ex.Message} Nothing was issued.");
        }
    }

    private async Task<X509Certificate2> LoadCaCertificateAsync(string secretName, CancellationToken cancellationToken)
    {
        var pem = await _vault.GetCertificatePemAsync(secretName, cancellationToken)
            ?? throw new CaRefusedException(
                $"{_vault.Name} has no secret '{secretName}'. Run 'init' first. Nothing was issued.");

        try
        {
            return Pem.LoadCertificate(pem, $"Secret '{secretName}'");
        }
        catch (InvalidDataException ex)
        {
            throw new CaRefusedException($"{ex.Message} Nothing was issued.");
        }
    }

    private async Task<bool> RegisterAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await _registry.RegisterCertificateAuthorityDeviceAsync(deviceId, cancellationToken);
            return true;
        }
        catch (DeviceAlreadyRegisteredException)
        {
            // Someone registered it in between. Fine if it is CA-authenticated; otherwise the
            // certificate just written is of no use to that identity, and the operator must know.
            var now = await _registry.GetAsync(deviceId, cancellationToken);
            if (now is { IsCertificateAuthority: true })
                return false;

            throw new CaRefusedException(
                $"Device '{deviceId}' was registered in {_registry.Name} with authentication type " +
                $"{now?.AuthenticationType ?? "unknown"} while the certificate was being issued. The registry " +
                "was not changed; the certificate files were written but cannot authenticate that identity.");
        }
    }
}

/// <summary>What <see cref="IssueCommand"/> produced.</summary>
/// <param name="CertificatePath">The device certificate, PEM.</param>
/// <param name="ChainPath">Device certificate followed by the issuing CA certificate, PEM.</param>
/// <param name="SerialNumber">The certificate's serial number, hexadecimal.</param>
/// <param name="NotAfter">The end of the certificate's validity.</param>
/// <param name="Registered">True when this run registered the device; false when it was registered already.</param>
public sealed record IssuedCertificate(
    string CertificatePath, string ChainPath, string SerialNumber, DateTimeOffset NotAfter, bool Registered);
