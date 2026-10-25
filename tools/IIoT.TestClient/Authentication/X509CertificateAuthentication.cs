using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;

namespace IIoT.TestClient.Authentication;

/// <summary>
/// Authenticates with an X.509 client certificate from the device CA (ADR-009): mutual TLS, and
/// no password at all.
/// </summary>
/// <remarks>
/// <para>
/// IoT Hub's "X.509 CA signed" mode accepts a device whose certificate chains to a verified CA
/// certificate and whose subject is <c>CN=&lt;deviceId&gt;</c>. The CN rule is checked here, before
/// anything connects: a mismatch otherwise surfaces only as a refused CONNECT with no reason given.
/// </para>
/// <para>
/// <strong>Only the device certificate is sent, not the chain.</strong> MQTTnet 5.2 hands TLS a
/// plain certificate collection (<c>SslClientAuthenticationOptions.ClientCertificates</c>), never
/// an <see cref="System.Net.Security.SslStreamCertificateContext"/>, so there is no way to pass
/// intermediates through it; and on Windows, building such a context with intermediates writes
/// them into the user's certificate store. Nothing is lost: IoT Hub has the issuing CA itself
/// registered, the device certificate's direct issuer, so the device certificate alone is a
/// complete path to it, and the handshake that crosses NB-IoT on every connection is smaller.
/// </para>
/// <para>
/// The private key is loaded from PEM and immediately re-imported through PKCS#12. Windows'
/// TLS stack (SChannel) refuses a client certificate whose key is ephemeral, which is what
/// <see cref="X509Certificate2.CreateFromPem(ReadOnlySpan{char}, ReadOnlySpan{char})"/> produces,
/// with "The credentials supplied to the package were not recognized". The PKCS#12 import puts the
/// key in a temporary key container instead, which on Windows is a file in the user's key store
/// until <see cref="Dispose"/> deletes it. Elsewhere the round trip is harmless, so it is done on
/// every platform and the CI runs the same code path as a Windows workstation.
/// </para>
/// </remarks>
public sealed class X509CertificateAuthentication : IDeviceAuthentication, IDisposable
{
    /// <summary>id-at-commonName (RFC 5280, appendix A).</summary>
    public const string CommonNameOid = "2.5.4.3";

    private readonly X509Certificate2 _certificate;

    private X509CertificateAuthentication(string deviceId, X509Certificate2 certificate)
    {
        DeviceId = deviceId;
        _certificate = certificate;
    }

    public string DeviceId { get; }

    /// <summary>The device certificate, with its private key, exactly as it is presented to the hub.</summary>
    public X509Certificate2 Certificate => _certificate;

    /// <summary>
    /// Loads the device certificate from <paramref name="certificateFile"/> and its key from
    /// <paramref name="keyFile"/>.
    /// </summary>
    /// <param name="deviceId">The device ID. Must be the certificate's CN.</param>
    /// <param name="certificateFile">
    /// PEM with the device certificate first: <c>&lt;id&gt;.chain.pem</c> or <c>&lt;id&gt;.cert.pem</c>
    /// as <c>IIoT.DeviceCa issue</c> writes them. Certificates after the first are not sent.
    /// </param>
    /// <param name="keyFile">The unencrypted private key, PEM, as <c>create-csr</c> writes it.</param>
    /// <param name="now">The time at which the certificate must be valid.</param>
    /// <exception cref="FileNotFoundException">Either file is missing.</exception>
    /// <exception cref="InvalidDataException">
    /// The files hold no certificate, a certificate for another device, one that is not valid now, or
    /// a key that does not belong to it.
    /// </exception>
    public static X509CertificateAuthentication Load(
        string deviceId, string certificateFile, string keyFile, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);

        return Create(deviceId, ReadFile(certificateFile, "Certificate"), certificateFile, ReadFile(keyFile, "Key"), keyFile, now);
    }

    /// <summary>As <see cref="Load"/>, from PEM text rather than files.</summary>
    public static X509CertificateAuthentication FromPem(
        string deviceId, string certificatePem, string keyPem, DateTimeOffset now) =>
        Create(deviceId, certificatePem, "the certificate PEM", keyPem, "the key PEM", now);

    /// <summary>
    /// The CN of <paramref name="certificate"/>'s subject, or <see langword="null"/> when it has no
    /// CN or more than one, either of which IoT Hub cannot map to a single device ID.
    /// </summary>
    public static string? CommonName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        string? commonName = null;
        foreach (var name in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (name.HasMultipleElements || name.GetSingleElementType().Value != CommonNameOid)
                continue;

            if (commonName is not null)
                return null;

            commonName = name.GetSingleElementValue();
        }

        return commonName;
    }

    /// <summary>No password: the TLS handshake has already proved the device's identity.</summary>
    public string? CreatePassword(string hostName, DateTimeOffset expiresAt) => null;

    /// <summary>
    /// Adds the device certificate, and a selection handler that always picks it.
    /// </summary>
    /// <remarks>
    /// The handler matters. Without one, .NET offers a certificate from the collection only if its
    /// chain matches one of the CA names the server lists in its certificate request, and if none
    /// matches it continues the handshake with no client certificate at all. IoT Hub does not
    /// document which names it lists. With the handler, the device presents its certificate
    /// whatever the list says, which is what a device with exactly one certificate should do.
    /// </remarks>
    public void ConfigureTls(MqttClientTlsOptionsBuilder tls)
    {
        ArgumentNullException.ThrowIfNull(tls);

        tls.WithClientCertificates(new X509Certificate2Collection(_certificate))
            .WithCertificateSelectionHandler(_ => _certificate);
    }

    /// <summary>Releases the key. On Windows this deletes its temporary key container.</summary>
    public void Dispose() => _certificate.Dispose();

    private static X509CertificateAuthentication Create(
        string deviceId, string certificatePem, string certificateSource, string keyPem, string keySource, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(certificatePem);
        ArgumentNullException.ThrowIfNull(keyPem);

        using var device = FirstCertificate(certificatePem, certificateSource);
        RequireDevice(device, deviceId, certificateSource);
        RequireValid(device, now, certificateSource);

        X509Certificate2 ephemeral;
        try
        {
            ephemeral = X509Certificate2.CreateFromPem(device.ExportCertificatePem(), keyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // Never echo the key text: the message names the file and says what was expected.
            throw new InvalidDataException(
                $"{keySource} holds no usable private key for the certificate in {certificateSource}. Expected the " +
                "unencrypted PEM key that create-csr wrote for this device, the one whose CSR the certificate " +
                $"was issued for. ({ex.Message})", ex);
        }

        using (ephemeral)
        {
            var pkcs12 = ephemeral.Export(X509ContentType.Pkcs12);
            try
            {
                return new X509CertificateAuthentication(deviceId, X509CertificateLoader.LoadPkcs12(pkcs12, password: null));
            }
            finally
            {
                // The export holds the private key in the clear.
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
    }

    private static string ReadFile(string path, string what) =>
        File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"{what} file not found: {path}", path);

    private static X509Certificate2 FirstCertificate(string pem, string source)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"{source} holds a malformed PEM certificate.", ex);
        }

        if (certificates.Count == 0)
            throw new InvalidDataException($"{source} holds no PEM certificate.");

        // The device certificate comes first, as in every chain file; the rest stay unused.
        for (var i = 1; i < certificates.Count; i++)
            certificates[i].Dispose();

        return certificates[0];
    }

    private static void RequireDevice(X509Certificate2 certificate, string deviceId, string source)
    {
        var commonName = CommonName(certificate);

        // Ordinal: IoT Hub device IDs are case-sensitive.
        if (commonName == deviceId)
            return;

        throw new InvalidDataException(commonName is null
            ? $"The certificate in {source} ({certificate.Subject}) has no single CN, so IoT Hub cannot match it " +
              $"to device '{deviceId}'. A CA-signed device certificate must have the subject CN=<deviceId>."
            : $"The certificate in {source} is for '{commonName}', not for device '{deviceId}'. IoT Hub " +
              "authenticates a CA-signed device only when the certificate's CN is its device ID; use that " +
              "device's certificate, or --device-id " + commonName + ".");
    }

    private static void RequireValid(X509Certificate2 certificate, DateTimeOffset now, string source)
    {
        var notBefore = new DateTimeOffset(certificate.NotBefore);
        var notAfter = new DateTimeOffset(certificate.NotAfter);

        if (now < notBefore || now > notAfter)
        {
            throw new InvalidDataException(
                $"The certificate in {source} is valid from {notBefore:u} to {notAfter:u}, not now ({now:u}). " +
                "IoT Hub would refuse it in the handshake.");
        }
    }
}
