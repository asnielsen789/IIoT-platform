using IIoT.TestClient.Fleet;
using IIoT.TestClient.IotHub;

namespace IIoT.TestClient.Configuration;

/// <summary>What one <c>fleet</c> run needs to know.</summary>
/// <param name="Hub">The hub, as typed; <c>null</c> only for a dry run.</param>
/// <param name="FleetFile">The fleet file.</param>
/// <param name="SecretsDirectory">Where each device's certificate and key are.</param>
/// <param name="Schedule">How far back, and how often devices measure and send.</param>
/// <param name="Devices">The devices to run, or empty for all of them.</param>
/// <param name="DryRun">Build and print the messages, connect to nothing.</param>
public sealed record FleetOptions(
    string? Hub,
    string FleetFile,
    string SecretsDirectory,
    FleetSchedule Schedule,
    IReadOnlyList<string> Devices,
    bool DryRun)
{
    /// <summary>The resolved hub hostname.</summary>
    public string HostName => IotHubHost.Resolve(Hub!);
}
