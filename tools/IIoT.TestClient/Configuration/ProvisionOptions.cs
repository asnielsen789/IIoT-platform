using IIoT.TestClient.Payloads;
using IIoT.TestClient.Provisioning;

namespace IIoT.TestClient.Configuration;

/// <summary>What one <c>provision</c> run needs to know.</summary>
/// <param name="IdScope">The DPS instance's ID scope.</param>
/// <param name="CertificateFile">The device certificate, PEM, device certificate first. Its CN is the registration ID.</param>
/// <param name="KeyFile">The private key the certificate was issued for.</param>
public sealed record ProvisionOptions(string IdScope, string CertificateFile, string KeyFile)
{
    /// <summary>The device provisioning host. The global endpoint unless a test points elsewhere.</summary>
    public string DpsHost { get; init; } = DpsEndpoint.GlobalHostName;

    /// <summary>False for <c>--no-send</c>: stop once the device is assigned.</summary>
    public bool Send { get; init; } = true;

    /// <summary>The <c>--fixture</c> selector for the message sent after assignment.</summary>
    public string Fixture { get; init; } = TelemetryFixtures.ValidSelector;
}
