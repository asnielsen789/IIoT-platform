using System.Globalization;
using IIoT.TestClient.Fleet;
using IIoT.TestClient.Payloads;
using IIoT.TestClient.Provisioning;

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

    /// <summary>Environment variable consulted when <c>--id-scope</c> is absent.</summary>
    public const string IdScopeVariable = "DPS_ID_SCOPE";

    /// <summary>The first argument that selects key and CSR generation instead of a run.</summary>
    public const string CreateCsrCommand = "create-csr";

    /// <summary>The first argument that selects registration through DPS instead of a run.</summary>
    public const string ProvisionCommand = "provision";

    /// <summary>The first argument that selects a fleet's simulated history instead of a run.</summary>
    public const string FleetCommand = "fleet";

    /// <summary>Usage text, printed for <c>--help</c> and for any usage error.</summary>
    public const string Usage = """
        IIoT.TestClient — simulated NB-IoT device publishing contract fixtures to Azure IoT Hub
        over raw MQTT/TLS.

        Usage:
          IIoT.TestClient --hub <name|hostname> --device-id <id> --device-key <key> [options]
          IIoT.TestClient --hub <name|hostname> --device-id <id> --cert <file> --key <file> [options]
          IIoT.TestClient --hub <name|hostname> --devices <path> --device-count <n> [options]
          IIoT.TestClient create-csr --device-id <id> [--out <dir>]
          IIoT.TestClient provision --id-scope <scope> --cert <file> --key <file> [options]
          IIoT.TestClient fleet --hub <name|hostname> [options]

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

        Zero-touch provisioning (provision):
          --id-scope <scope>      The DPS instance's ID scope. Falls back to DPS_ID_SCOPE.
          --cert <file>           The device certificate, as above. Its CN is the registration ID,
                                  and DPS registers the device in IoT Hub under that ID.
          --key <file>            The private key create-csr wrote for that certificate.
          --dps-host <host>       Default: global.azure-devices-provisioning.net.
          --fixture <selector>    The one message sent to the assigned hub. Default: valid.
          --no-send               Stop once DPS has assigned the device.
          Registers with DPS, waits for the assignment, then connects to the hub DPS named and
          sends one message.

        A simulated fleet (fleet):
          --hub <name|hostname>   IoT Hub. Falls back to IOT_HUB. Not needed with --dry-run.
          --fleet <file>          The fleet file. Default: the reference fleet, staging-fleet.json.
          --secrets <dir>         Where each device's <id>.chain.pem (or .cert.pem) and <id>.key.pem
                                  are. Default: ~/.secrets/iiot. A device without them is skipped.
          --hours <n>             How far back each device's history reaches. Default 24.
          --sample-every <min>    How often a device measures. Default 60.
          --send-every <min>      How often it sends what it measured. Default 240.
          --device <id>           Only this device; repeatable. Default: every device in the file.
          --dry-run               Print each device's newest message, connect to nothing.
          Each device connects with its own certificate and sends its history as buffered
          messages, tanks, chargers, car washes and oil separators alike (ADR-011). The hub must
          know the devices already: provision them first.
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

    /// <summary>Whether <paramref name="args"/> asks for <c>provision</c> rather than a run.</summary>
    public static bool IsProvision(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && args[0] == ProvisionCommand;
    }

    /// <summary>Parses a <c>provision</c> command line, starting with the command itself.</summary>
    /// <param name="args">The raw command line, <c>provision</c> first.</param>
    /// <param name="environment">Environment lookup, for the <c>DPS_ID_SCOPE</c> fallback.</param>
    /// <returns>The options, or <see langword="null"/> when help was requested.</returns>
    /// <exception cref="CommandLineException">The arguments do not form the command.</exception>
    public static ProvisionOptions? ParseProvision(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!IsProvision(args))
            throw new CommandLineException($"Expected '{ProvisionCommand}' as the first argument.");

        string? idScope = null;
        string? certificateFile = null;
        string? keyFile = null;
        string? dpsHost = null;
        string? fixture = null;
        var send = true;

        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "-h" or "--help":
                    return null;
                case "--id-scope":
                    idScope = Value(args, ref i);
                    break;
                case "--cert":
                    certificateFile = Value(args, ref i);
                    break;
                case "--key":
                    keyFile = Value(args, ref i);
                    break;
                case "--dps-host":
                    dpsHost = Value(args, ref i);
                    break;
                case "--fixture":
                    fixture = Value(args, ref i);
                    break;
                case "--no-send":
                    send = false;
                    break;
                default:
                    // Includes --hub and --device-id: DPS names the hub, and the certificate's CN
                    // is the device ID, so either one here would be a second answer to a settled question.
                    throw new CommandLineException($"{ProvisionCommand} does not take '{argument}'.");
            }
        }

        idScope = Trimmed(idScope) ?? Trimmed(environment(IdScopeVariable));
        if (idScope is null)
            throw new CommandLineException($"{ProvisionCommand} needs --id-scope (or {IdScopeVariable}).");

        certificateFile = Trimmed(certificateFile);
        keyFile = Trimmed(keyFile);
        if (certificateFile is null || keyFile is null)
        {
            throw new CommandLineException(
                $"{ProvisionCommand} needs --cert and --key: DPS authenticates the device by its certificate.");
        }

        if (!send && fixture is not null)
            throw new CommandLineException("--fixture chooses the message to send; with --no-send nothing is sent.");

        return new ProvisionOptions(idScope, certificateFile, keyFile)
        {
            DpsHost = Trimmed(dpsHost) ?? DpsEndpoint.GlobalHostName,
            Send = send,
            Fixture = Trimmed(fixture) ?? TelemetryFixtures.ValidSelector
        };
    }

    /// <summary>Whether <paramref name="args"/> asks for <c>fleet</c> rather than a run.</summary>
    public static bool IsFleet(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && args[0] == FleetCommand;
    }

    /// <summary>Parses a <c>fleet</c> command line, starting with the command itself.</summary>
    /// <param name="args">The raw command line, <c>fleet</c> first.</param>
    /// <param name="environment">Environment lookup, for the <c>IOT_HUB</c> fallback.</param>
    /// <param name="defaultFleetFile">The fleet file when <c>--fleet</c> is absent.</param>
    /// <param name="defaultSecrets">The secrets directory when <c>--secrets</c> is absent.</param>
    /// <returns>The options, or <see langword="null"/> when help was requested.</returns>
    /// <exception cref="CommandLineException">The arguments do not form the command.</exception>
    public static FleetOptions? ParseFleet(
        string[] args, Func<string, string?> environment, string defaultFleetFile, string defaultSecrets)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!IsFleet(args))
            throw new CommandLineException($"Expected '{FleetCommand}' as the first argument.");

        string? hub = null;
        string? fleetFile = null;
        string? secrets = null;
        var schedule = FleetSchedule.Default;
        var devices = new List<string>();
        var dryRun = false;

        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "-h" or "--help":
                    return null;
                case "--hub":
                    hub = Value(args, ref i);
                    break;
                case "--fleet":
                    fleetFile = Value(args, ref i);
                    break;
                case "--secrets":
                    secrets = Value(args, ref i);
                    break;
                case "--hours":
                    schedule = schedule with { Window = TimeSpan.FromHours(Integer(args, ref i)) };
                    break;
                case "--sample-every":
                    schedule = schedule with { SampleEvery = TimeSpan.FromMinutes(Integer(args, ref i)) };
                    break;
                case "--send-every":
                    schedule = schedule with { SendEvery = TimeSpan.FromMinutes(Integer(args, ref i)) };
                    break;
                case "--device":
                    devices.Add(Value(args, ref i).Trim());
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    // Includes --cert and --device-key: each device's credential is found by its ID.
                    throw new CommandLineException($"{FleetCommand} does not take '{argument}'.");
            }
        }

        if (schedule.Problem() is { } problem)
            throw new CommandLineException(problem);

        hub = Trimmed(hub) ?? Trimmed(environment(HubVariable));
        if (hub is null && !dryRun)
            throw new CommandLineException($"{FleetCommand} needs --hub (or {HubVariable}), unless it is a --dry-run.");

        return new FleetOptions(
            hub,
            Trimmed(fleetFile) ?? defaultFleetFile,
            Trimmed(secrets) ?? defaultSecrets,
            schedule,
            devices,
            dryRun);
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
