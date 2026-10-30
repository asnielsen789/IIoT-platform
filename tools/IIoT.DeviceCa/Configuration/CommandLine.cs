namespace IIoT.DeviceCa.Configuration;

/// <summary>Parses the tool's command line into one of its three commands.</summary>
/// <remarks>
/// Hand-rolled like the test client's parser: three commands and a handful of options do not
/// justify a package. There is deliberately no environment-variable fallback: every vault and hub
/// this tool writes to is named on the command line, where the operator sees it.
/// </remarks>
public static class CommandLine
{
    /// <summary>Usage text, printed for <c>--help</c> and for any usage error.</summary>
    public const string Usage = """
        IIoT.DeviceCa — the device certificate authority's signing tool (ADR-009). The CA keys stay
        in Key Vault; this tool builds certificates and has Key Vault sign them.

        Usage:
          IIoT.DeviceCa init --vault <name> [--force]
              Create the root and issuing CA certificates and store them in the vault as the
              secrets device-ca-root-certificate and device-ca-issuing-certificate. Refuses to
              replace existing ones without --force: devices already issued chain to them.

          IIoT.DeviceCa issue --vault <name> (--hub <name> | --no-register) --csr <file> --device-id <id> [--out <dir>]
              Issue a device certificate for the key in a CSR, with subject CN=<id> whatever the
              CSR asks for, write <id>.cert.pem and <id>.chain.pem (device and issuing CA) to
              <dir> (default: the current directory), and register the device in the hub as
              CA-authenticated. With --no-register, register nothing: the device provisions
              itself through DPS, so <id> must also be a valid DPS registration ID.

          IIoT.DeviceCa ca-certificate --vault <name> [--out <file>]
              Write the issuing CA certificate, the one to register in IoT Hub, to <file> or to
              standard output.

        Options:
          --vault <name|uri>      Key Vault holding the CA. A bare name gains '.vault.azure.net'.
          --hub <name|hostname>   IoT Hub. A bare name gains '.azure-devices.net'.
          --csr <file>            The device's certificate signing request, PEM.
          --device-id <id>        The IoT Hub device ID, and the certificate's subject.
          --out <dir|file>        Where to write.
          --no-register           issue only, instead of --hub: leave the registration to DPS.
          --force                 init only: replace existing CA certificates.
          -h, --help              This text.

        Signs in as you, through the Azure CLI (az login) or another developer sign-in.
        """;

    /// <summary>Parses <paramref name="args"/>.</summary>
    /// <returns>The command, or <see langword="null"/> when help was requested.</returns>
    /// <exception cref="CommandLineException">The arguments do not form a command.</exception>
    public static DeviceCaCommand? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
            throw new CommandLineException("No command given.");

        if (args[0] is "-h" or "--help")
            return null;

        var command = args[0];
        if (command is not ("init" or "issue" or "ca-certificate"))
            throw new CommandLineException($"Unknown command '{command}'.");

        string? vault = null, hub = null, csr = null, deviceId = null, output = null;
        string? force = null, noRegister = null;

        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "-h" or "--help":
                    return null;
                case "--vault":
                    vault = Value(args, ref i);
                    break;
                case "--hub":
                    hub = Value(args, ref i);
                    break;
                case "--csr":
                    csr = Value(args, ref i);
                    break;
                case "--device-id":
                    deviceId = Value(args, ref i);
                    break;
                case "--out":
                    output = Value(args, ref i);
                    break;
                case "--force":
                    force = argument;
                    break;
                case "--no-register":
                    noRegister = argument;
                    break;
                default:
                    throw new CommandLineException($"Unknown option '{argument}'.");
            }
        }

        switch (command)
        {
            case "init":
                RejectUnused(
                    command, ("--hub", hub), ("--csr", csr), ("--device-id", deviceId), ("--out", output),
                    ("--no-register", noRegister));
                return new InitCommandLine(Required(vault, "--vault", command), force is not null);

            case "issue":
                RejectUnused(command, ("--force", force));

                // Exactly one of the two: the hub is only used to register, and leaving it out by
                // accident must not silently skip the registration.
                if (noRegister is not null && hub is not null)
                {
                    throw new CommandLineException(
                        "issue takes --hub or --no-register, not both: the hub is only used to register the device.");
                }

                if (noRegister is null && string.IsNullOrWhiteSpace(hub))
                {
                    throw new CommandLineException(
                        "issue needs --hub, or --no-register when the device provisions itself through DPS.");
                }

                return new IssueCommandLine(
                    Required(vault, "--vault", command),
                    noRegister is null ? hub!.Trim() : null,
                    Required(csr, "--csr", command),
                    Required(deviceId, "--device-id", command),
                    string.IsNullOrWhiteSpace(output) ? "." : output);

            default:
                RejectUnused(
                    command, ("--hub", hub), ("--csr", csr), ("--device-id", deviceId), ("--force", force),
                    ("--no-register", noRegister));
                return new CaCertificateCommandLine(
                    Required(vault, "--vault", command), string.IsNullOrWhiteSpace(output) ? null : output);
        }
    }

    private static string Required(string? value, string option, string command) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new CommandLineException($"{command} needs {option}.")
            : value.Trim();

    // An option the command does not use is an error, not something to ignore: an operator who
    // passes --force to issue, or --hub to init, expects it to do something.
    private static void RejectUnused(string command, params (string Option, string? Value)[] options)
    {
        foreach (var (option, value) in options)
        {
            if (value is not null)
                throw new CommandLineException($"{command} does not take {option}.");
        }
    }

    private static string Value(string[] args, ref int index)
    {
        var name = args[index];
        if (index + 1 >= args.Length)
            throw new CommandLineException($"{name} needs a value.");

        return args[++index];
    }
}

/// <summary>One of the tool's commands, parsed.</summary>
public abstract record DeviceCaCommand;

/// <summary><c>init</c>: create and store the CA certificates.</summary>
public sealed record InitCommandLine(string Vault, bool Force) : DeviceCaCommand;

/// <summary><c>issue</c>: issue a device certificate and register the device.</summary>
/// <param name="Hub">The hub to register the device in, or <see langword="null"/> with <c>--no-register</c>.</param>
public sealed record IssueCommandLine(string Vault, string? Hub, string CsrFile, string DeviceId, string OutputDirectory)
    : DeviceCaCommand;

/// <summary><c>ca-certificate</c>: export the issuing CA certificate.</summary>
public sealed record CaCertificateCommandLine(string Vault, string? OutputFile) : DeviceCaCommand;
