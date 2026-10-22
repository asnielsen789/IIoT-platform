using Azure;
using Azure.Core;
using Azure.Identity;
using IIoT.DeviceCa.Commands;
using IIoT.DeviceCa.Configuration;
using IIoT.DeviceCa.IotHub;
using IIoT.DeviceCa.KeyVault;
using Microsoft.Azure.Devices.Common.Exceptions;

namespace IIoT.DeviceCa;

/// <summary>
/// Entry point. Thin on purpose: the certificate profiles and the commands' rules live in classes
/// that are tested without Azure; this only wires them to Key Vault and IoT Hub.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        DeviceCaCommand? command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (CommandLineException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(CommandLine.Usage);
            return 2;
        }

        if (command is null)
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
            if (OperatorSignIn.Problem(Environment.GetEnvironmentVariable(OperatorSignIn.SelectionVariable)) is { } signInProblem)
                throw new CaRefusedException(signInProblem);

            await RunAsync(command, CreateCredential(), cancellation.Token);
            return 0;
        }
        catch (CaRefusedException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            await Console.Error.WriteLineAsync(
                $"Key Vault refused the request ({ex.ErrorCode}). Signing needs Key Vault Crypto User and the CA " +
                "certificates need Key Vault Secrets Officer, both on the vault; see infra/README.md, " +
                "\"Device certificate authority (one-time)\". A new role assignment can take a few minutes to apply.");
            return 1;
        }
        catch (UnauthorizedException ex)
        {
            await Console.Error.WriteLineAsync(
                $"IoT Hub refused the request ({ex.Message.Split('\n')[0].Trim()}). Registering devices needs " +
                "IoT Hub Registry Contributor on the hub; see infra/README.md.");
            return 1;
        }
        catch (CredentialUnavailableException ex)
        {
            await Console.Error.WriteLineAsync($"No sign-in found. Run 'az login' first. ({ex.Message.Split('\n')[0]})");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Stopped.");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The operator's own sign-in. ADR-009: the tool "is run by an operator, never by the Function
    /// or CI", so the credentials a machine or a pipeline would bring — environment variables,
    /// workload identity, managed identity — are excluded, and only developer sign-ins remain:
    /// in Azure.Identity 1.21's order Visual Studio, VS Code, Azure CLI, Azure PowerShell, Azure
    /// Developer CLI and the broker. <c>AZURE_TOKEN_CREDENTIALS</c> can override the exclusions,
    /// so <c>Main</c> checks it first (<see cref="OperatorSignIn"/>).
    /// </summary>
    internal static TokenCredential CreateCredential() => new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeEnvironmentCredential = true,
        ExcludeWorkloadIdentityCredential = true,
        ExcludeManagedIdentityCredential = true
    });

    private static async Task RunAsync(DeviceCaCommand command, TokenCredential credential, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case InitCommandLine init:
                await new InitCommand(Vault(init.Vault, credential), TimeProvider.System, Console.Out)
                    .RunAsync(init.Force, cancellationToken);
                break;

            case IssueCommandLine issue:
                using (var registry = new IotHubDeviceRegistry(IotHubDeviceRegistry.ResolveHostName(issue.Hub), credential))
                {
                    await new IssueCommand(Vault(issue.Vault, credential), registry, TimeProvider.System, Console.Out)
                        .RunAsync(issue.CsrFile, issue.DeviceId, issue.OutputDirectory, cancellationToken);
                }

                break;

            case CaCertificateCommandLine export:
                await new CaCertificateCommand(Vault(export.Vault, credential), Console.Out)
                    .RunAsync(export.OutputFile, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Unhandled command {command.GetType().Name}.");
        }
    }

    private static KeyVaultCa Vault(string nameOrUri, TokenCredential credential) =>
        new(KeyVaultCa.ResolveUri(nameOrUri), credential);
}
