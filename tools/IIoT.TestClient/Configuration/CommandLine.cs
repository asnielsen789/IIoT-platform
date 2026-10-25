using System.Globalization;
using IIoT.TestClient.Payloads;

namespace IIoT.TestClient.Configuration;

/// <summary>
/// Parses the command line, falling back to environment variables.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a package: the option set is small, and the point of the
/// fallback is that a key can be passed in the environment instead of in a shell history. There is
/// deliberately no configuration file in the chain — nothing in this tool can read a secret from a
/// file that might end up committed.
/// </remarks>
public static class CommandLine
{
    /// <summary>Environment variable consulted when <c>--hub</c> is absent.</summary>
    public const string HubVariable = "IOT_HUB";

    /// <summary>Environment variable consulted when <c>--device-id</c> is absent.</summary>
    public const string DeviceIdVariable = "DEVICE_ID";

    /// <summary>Environment variable consulted when <c>--device-key</c> is absent.</summary>
    public const string DeviceKeyVariable = "DEVICE_KEY";

    /// <summary>Environment variable consulted when <c>--devices</c> is absent.</summary>
    public const string DevicesFileVariable = "DEVICES_FILE";

    /// <summary>The first argument that selects key and CSR generation instead of a run.</summary>
    public const string CreateCsrCommand = "create-csr";

    /// <summary>Usage text, printed for <c>--help</c> and for any usage error.</summary>
    public const string Usage = """
        IIoT.TestClient — simulated NB-IoT device publishing contract fixtures to Azure IoT Hub
        over raw MQTT/TLS.

        Usage:
          IIoT.TestClient --hub <name|hostname> --device-id <id> --device-key <key> [options]
          IIoT.TestClient --hub <name|hostname> --device-id <id> --cert <file> --key <file> [options]
          IIoT.TestClient --hub <name|hostname> --devices <path> --device-count <n> [options]
          IIoT.TestClient create-csr --device-id <id> [--out <dir>]

        Target:
          --hub <name|hostname>   IoT Hub. A bare name gains '.azure-devices.net'.
                                  Falls back to IOT_HUB.

        Identity (one of):
          --device-id <id>        Registered device identity. Falls back to DEVICE_ID.
          --device-key <key>      That device's base64 shared access key. Falls back to DEVICE_KEY.
          --cert <file>           That device's X.509 certificate, PEM, device certificate first:
                                  the <id>.chain.pem or <id>.cert.pem IIoT.DeviceCa issue wrote.
                                  Its CN must be the device ID. Only the device certificate is
                                  sent; IoT Hub holds the issuing CA.
          --key <file>            The private key create-csr wrote for that certificate. With
                                  --cert and --key, DEVICE_KEY and DEVICES_FILE are ignored.
          --devices <path>        JSON file: [{ "deviceId": "...", "key": "..." }, ...].
                                  Falls back to DEVICES_FILE. NEVER COMMIT THIS FILE.

        Fleet and rate:
          --device-count <n>      Simulate n devices concurrently, taking the first n entries of
                                  the devices file. Default 1.
          --interval <seconds>    Delay between messages, per device. Default 60.
          --count <n>             Messages per device, then stop. Default: run until Ctrl+C.
          --once                  Shorthand for --count 1.

        Payload:
          --fixture <selector>    all | valid | invalid | a fixture name such as
                                  valid-buffered-backfill. Default: valid.
          --no-mutate             Send fixture bytes verbatim, for contract and negative testing.
                                  Without it, messageId, sentAt, deviceId and every measuredAt are
                                  rewritten so each message is fresh and passes the identity check.

        Other:
          --sas-lifetime <min>    SAS token validity in minutes. Default 60. Not with --cert.
          --dry-run               Build and print messages without connecting to anything.
                                  With --cert, also checks the certificate and key files.
          -h, --help              This text.

        Key and CSR (create-csr):
          --device-id <id>        The device the key is for, and the CSR's CN. Falls back to
                                  DEVICE_ID.
          --out <dir>             Where to write. Default: ~/.secrets/iiot, outside the repo.
          Writes <id>.key.pem, a new EC P-256 private key (NEVER COMMIT OR SHARE IT), and
          <id>.csr.pem, the request for IIoT.DeviceCa issue --csr. Never overwrites a file.
        """;

    /// <summary>Parses <paramref name="args"/> into a validated configuration.</summary>
    /// <param name="args">The raw command line.</param>
    /// <param name="environment">
    /// Environment lookup. Injected so the fallback is testable without touching the process
    /// environment.
    /// </param>
    /// <returns>
    /// The options, or <see langword="null"/> when help was requested and nothing should run.
    /// </returns>
    /// <exception cref="CommandLineException">The arguments cannot produce a runnable configuration.</exception>
    public static TestClientOptions? Parse(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        string? hub = null;
        string? deviceId = null;
        string? deviceKey = null;
        string? devicesFile = null;
        string? certificateFile = null;
        string? keyFile = null;
        var deviceCount = 1;
        var interval = TimeSpan.FromSeconds(60);
        int? messageCount = null;
        var fixture = TelemetryFixtures.ValidSelector;
        var mutate = true;
        var dryRun = false;
        TimeSpan? sasLifetime = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "-h" or "--help":
                    return null;
                case "--hub":
                    hub = Value(args, ref i);
                    break;
                case "--device-id":
                    deviceId = Value(args, ref i);
                    break;
                case "--device-key":
                    deviceKey = Value(args, ref i);
                    break;
                case "--devices":
                    devicesFile = Value(args, ref i);
                    break;
                case "--cert":
                    certificateFile = Value(args, ref i);
                    break;
                case "--key":
                    keyFile = Value(args, ref i);
                    break;
                case "--device-count":
                    deviceCount = Integer(args, ref i);
                    break;
                case "--interval":
                    interval = TimeSpan.FromSeconds(Number(args, ref i));
                    break;
                case "--count":
                    messageCount = Integer(args, ref i);
                    break;
                case "--once":
                    messageCount = 1;
                    break;
                case "--fixture":
                    fixture = Value(args, ref i);
                    break;
                case "--no-mutate":
                    mutate = false;
                    break;
                case "--sas-lifetime":
                    sasLifetime = TimeSpan.FromMinutes(Number(args, ref i));
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new CommandLineException($"Unknown option '{argument}'.");
            }
        }

        hub ??= environment(HubVariable);
        deviceId ??= environment(DeviceIdVariable);

        // --cert or --key on the command line chooses the mechanism. The shared access fallbacks are
        // then not consulted: a DEVICE_KEY left in the environment from an earlier SAS session must
        // not make a certificate run ambiguous. Only what is typed next to --cert can conflict with it.
        var usesCertificate = certificateFile is not null || keyFile is not null;
        if (!usesCertificate)
        {
            deviceKey ??= environment(DeviceKeyVariable);
            devicesFile ??= environment(DevicesFileVariable);
        }
        else if (sasLifetime is not null)
        {
            throw new CommandLineException(
                "--sas-lifetime applies to shared access keys; a certificate has no token to expire.");
        }

        if (string.IsNullOrWhiteSpace(hub))
            throw new CommandLineException($"--hub is required (or set {HubVariable}).");

        var options = new TestClientOptions
        {
            Hub = hub,
            DeviceId = Trimmed(deviceId),
            DeviceKey = Trimmed(deviceKey),
            DevicesFile = Trimmed(devicesFile),
            CertificateFile = Trimmed(certificateFile),
            KeyFile = Trimmed(keyFile),
            DeviceCount = deviceCount,
            Interval = interval,
            MessageCount = messageCount,
            Fixture = fixture,
            Mutate = mutate,
            DryRun = dryRun,
            SasLifetime = sasLifetime ?? TimeSpan.FromHours(1)
        };

        options.Validate();
        return options;
    }

    /// <summary>Parses using the real process environment.</summary>
    public static TestClientOptions? Parse(string[] args) =>
        Parse(args, Environment.GetEnvironmentVariable);

    /// <summary>Whether <paramref name="args"/> asks for <c>create-csr</c> rather than a run.</summary>
    public static bool IsCreateCsr(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && args[0] == CreateCsrCommand;
    }

    /// <summary>Parses a <c>create-csr</c> command line, starting with the command itself.</summary>
    /// <param name="args">The raw command line, <c>create-csr</c> first.</param>
    /// <param name="environment">Environment lookup, for the <c>DEVICE_ID</c> fallback.</param>
    /// <param name="defaultDirectory">Where to write when <c>--out</c> is absent.</param>
    /// <returns>The options, or <see langword="null"/> when help was requested.</returns>
    /// <exception cref="CommandLineException">The arguments do not form the command.</exception>
    public static CreateCsrOptions? ParseCreateCsr(
        string[] args, Func<string, string?> environment, string defaultDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);

        if (!IsCreateCsr(args))
            throw new CommandLineException($"Expected '{CreateCsrCommand}' as the first argument.");

        string? deviceId = null;
        string? directory = null;

        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "-h" or "--help":
                    return null;
                case "--device-id":
                    deviceId = Value(args, ref i);
                    break;
                case "--out":
                    directory = Value(args, ref i);
                    break;
                default:
                    // Includes every run option: create-csr connects to nothing, so --hub or --cert
                    // here is a mistake worth pointing out rather than ignoring.
                    throw new CommandLineException($"{CreateCsrCommand} does not take '{argument}'.");
            }
        }

        deviceId = Trimmed(deviceId) ?? Trimmed(environment(DeviceIdVariable));
        if (deviceId is null)
            throw new CommandLineException($"{CreateCsrCommand} needs --device-id (or {DeviceIdVariable}).");

        return new CreateCsrOptions(deviceId, Trimmed(directory) ?? defaultDirectory);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Value(string[] args, ref int index)
    {
        var name = args[index];
        if (index + 1 >= args.Length)
            throw new CommandLineException($"{name} needs a value.");

        return args[++index];
    }

    private static int Integer(string[] args, ref int index)
    {
        var name = args[index];
        var raw = Value(args, ref index);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new CommandLineException($"{name} needs a whole number, not '{raw}'.");
    }

    private static double Number(string[] args, ref int index)
    {
        var name = args[index];
        var raw = Value(args, ref index);

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new CommandLineException($"{name} needs a number, not '{raw}'.");
    }
}
