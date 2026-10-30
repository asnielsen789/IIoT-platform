using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.Fleet;
using IIoT.TestClient.Payloads;
using IIoT.TestClient.Provisioning;
using IIoT.TestClient.Simulation;

namespace IIoT.TestClient;

/// <summary>
/// Entry point. Deliberately thin — everything worth testing lives in the classes it calls, since
/// nothing that needs an IoT Hub can be tested at all yet.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (CommandLine.IsCreateCsr(args))
            return await CreateCsrAsync(args);

        if (CommandLine.IsProvision(args))
            return await ProvisionAsync(args);

        if (CommandLine.IsFleet(args))
            return await FleetAsync(args);

        TestClientOptions? options;
        try
        {
            options = CommandLine.Parse(args);
        }
        catch (CommandLineException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(CommandLine.Usage);
            return 2;
        }

        if (options is null)
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Handle Ctrl+C ourselves so the run stops at a message boundary and each device can
            // still send a DISCONNECT.
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var runner = new SimulationRunner(
            options, TelemetryFixtures.FromOutputDirectory(), Console.Out);

        try
        {
            await runner.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            // Terse by design: an exception message here can carry a hostname, and the message is
            // the useful part anyway. Nothing logs the device key, which is never put in a message.
            await Console.Error.WriteLineAsync($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <c>create-csr</c>: a new key pair and CSR, as device firmware would make them, for
    /// <c>IIoT.DeviceCa issue</c>. Connects to nothing.
    /// </summary>
    private static async Task<int> CreateCsrAsync(string[] args)
    {
        CreateCsrOptions? options;
        try
        {
            options = CommandLine.ParseCreateCsr(args, Environment.GetEnvironmentVariable, DeviceKeyFiles.DefaultDirectory);
        }
        catch (CommandLineException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(CommandLine.Usage);
            return 2;
        }

        if (options is null)
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        WrittenKeyFiles written;
        try
        {
            written = DeviceKeyFiles.Create(options.DeviceId, options.OutputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Created an EC P-256 key and a CSR for {options.DeviceId}:");
        Console.WriteLine($"  {written.KeyPath}  (private key: stays with the device; never commit or share it)");
        Console.WriteLine($"  {written.CsrPath}  (certificate signing request: give this one to the CA)");
        Console.WriteLine();
        Console.WriteLine("Next, issue the certificate (tools/IIoT.DeviceCa/README.md):");
        Console.WriteLine(
            $"  IIoT.DeviceCa issue --vault <vault> --hub <hub> --csr \"{written.CsrPath}\" " +
            $"--device-id {options.DeviceId} --out \"{options.OutputDirectory}\"");
        Console.WriteLine("For a device DPS registers (provision), use --no-register instead of --hub.");
        return 0;
    }

    /// <summary>
    /// <c>fleet</c>: every device of a fleet file sends its simulated history with its own
    /// certificate (#79).
    /// </summary>
    private static async Task<int> FleetAsync(string[] args)
    {
        FleetOptions? options;
        try
        {
            options = CommandLine.ParseFleet(args, Environment.GetEnvironmentVariable, FleetFile.ReferencePath, DeviceKeyFiles.DefaultDirectory);
        }
        catch (CommandLineException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(CommandLine.Usage);
            return 2;
        }

        if (options is null)
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await new FleetRunner(options, Console.Out).RunAsync(cancellation.Token) == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Stopped.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            await Console.Error.WriteLineAsync($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <c>provision</c>: register through DPS with the device certificate, then send one message to
    /// the hub DPS assigned (#48).
    /// </summary>
    private static async Task<int> ProvisionAsync(string[] args)
    {
        ProvisionOptions? options;
        try
        {
            options = CommandLine.ParseProvision(args, Environment.GetEnvironmentVariable);
        }
        catch (CommandLineException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(CommandLine.Usage);
            return 2;
        }

        if (options is null)
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var runner = new ProvisioningRunner(options, TelemetryFixtures.FromOutputDirectory(), Console.Out);

        try
        {
            return await runner.RunAsync(cancellation.Token) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            // As for a run: the message, never the key, which is in no message.
            await Console.Error.WriteLineAsync($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
