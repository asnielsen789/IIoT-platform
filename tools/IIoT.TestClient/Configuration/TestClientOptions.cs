using IIoT.TestClient.IotHub;
using IIoT.TestClient.Payloads;

namespace IIoT.TestClient.Configuration;

/// <summary>Everything one run of the client needs to know.</summary>
public sealed record TestClientOptions
{
    /// <summary>The hub name or hostname, as supplied.</summary>
    public required string Hub { get; init; }

    /// <summary>Single-device identity, or <see langword="null"/> when a devices file is used.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Single-device key, or <see langword="null"/> when a devices file is used.</summary>
    public string? DeviceKey { get; init; }

    /// <summary>Path to a runtime-supplied fleet file, or <see langword="null"/>.</summary>
    public string? DevicesFile { get; init; }

    /// <summary>
    /// The device certificate (PEM, device certificate first) for X.509 authentication, or
    /// <see langword="null"/> for a shared access key.
    /// </summary>
    public string? CertificateFile { get; init; }

    /// <summary>The private key for <see cref="CertificateFile"/>, PEM, or <see langword="null"/>.</summary>
    public string? KeyFile { get; init; }

    /// <summary>True when the device authenticates with a certificate rather than a key.</summary>
    public bool UsesCertificate => CertificateFile is not null || KeyFile is not null;

    /// <summary>How many devices to simulate concurrently.</summary>
    public int DeviceCount { get; init; } = 1;

    /// <summary>Delay between messages, per device.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Messages per device, or <see langword="null"/> to run until interrupted.</summary>
    public int? MessageCount { get; init; }

    /// <summary>The <c>--fixture</c> selector.</summary>
    public string Fixture { get; init; } = TelemetryFixtures.ValidSelector;

    /// <summary>False for <c>--no-mutate</c>: send fixture bytes verbatim.</summary>
    public bool Mutate { get; init; } = true;

    /// <summary>True for <c>--dry-run</c>: build and print messages, connect to nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>How long a generated SAS token stays valid.</summary>
    public TimeSpan SasLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The resolved IoT Hub hostname.</summary>
    public string HostName => IotHubHost.Resolve(Hub);

    /// <summary>
    /// Checks the combinations the parser cannot check field by field.
    /// </summary>
    /// <exception cref="CommandLineException">The combination cannot be run.</exception>
    public void Validate()
    {
        if (UsesCertificate)
        {
            ValidateCertificate();
        }
        else if (DevicesFile is null)
        {
            if (string.IsNullOrWhiteSpace(DeviceId) || string.IsNullOrWhiteSpace(DeviceKey))
            {
                throw new CommandLineException(
                    "Supply either --device-id and --device-key (or DEVICE_ID and DEVICE_KEY), " +
                    "--device-id with --cert and --key, or --devices <path>.");
            }

            if (DeviceCount > 1)
            {
                throw new CommandLineException(
                    "--device-count above 1 needs --devices <path>; a single key authenticates a " +
                    "single identity.");
            }
        }

        if (DeviceCount < 1)
            throw new CommandLineException("--device-count must be at least 1.");

        if (Interval < TimeSpan.Zero)
            throw new CommandLineException("--interval cannot be negative.");

        if (MessageCount is < 1)
            throw new CommandLineException("--count must be at least 1.");

        if (SasLifetime <= TimeSpan.Zero)
            throw new CommandLineException("--sas-lifetime must be positive.");
    }

    private void ValidateCertificate()
    {
        if (CertificateFile is null || KeyFile is null)
        {
            throw new CommandLineException(
                "--cert and --key go together: the device certificate and the private key it was issued for.");
        }

        if (DeviceKey is not null)
        {
            throw new CommandLineException(
                "--device-key and --cert are two ways to authenticate the same device; use one.");
        }

        // Certificates in multi-device mode would need a certificate and a key per device in the
        // devices file, a format of their own. One client per certificate covers the tests #11 needs.
        if (DevicesFile is not null || DeviceCount > 1)
        {
            throw new CommandLineException(
                "Certificate authentication covers one device: --cert cannot be combined with --devices or " +
                "a --device-count above 1. Run one client per certificate.");
        }

        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            throw new CommandLineException(
                "--cert needs --device-id (or DEVICE_ID): the device the certificate was issued for.");
        }
    }
}
