using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.Simulation;
using IIoT.TestClient.Tests.Fixtures;
using IIoT.TestClient.Tests.TestDoubles;

namespace IIoT.TestClient.Tests.Simulation;

/// <summary>
/// A certificate run, end to end without a network: <c>--dry-run</c> loads and checks the
/// certificate and key, and connects to nothing.
/// </summary>
public sealed class SimulationRunnerCertificateTests : IDisposable
{
    private const string DeviceId = "tank-000001";

    private readonly TestPki _pki = new(DateTimeOffset.UtcNow);
    private readonly string _directory = Directory.CreateTempSubdirectory("iiot-testclient-").FullName;
    private readonly string _chainFile;
    private readonly string _keyFile;

    public SimulationRunnerCertificateTests()
    {
        var written = DeviceKeyFiles.Create(DeviceId, _directory);
        var csr = new DeviceCsr(DeviceId, File.ReadAllText(written.KeyPath), File.ReadAllText(written.CsrPath));
        using var issued = _pki.Issue(csr);

        _keyFile = written.KeyPath;
        _chainFile = Path.Combine(_directory, $"{DeviceId}.chain.pem");
        File.WriteAllText(_chainFile, _pki.ChainPem(issued));
    }

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task DryRun_WithACertificate_ReportsItAndTheUsualUserName()
    {
        var log = new StringWriter();

        await new SimulationRunner(Options(DeviceId), FixtureSource.Store, log).RunAsync(CancellationToken.None);

        var output = log.ToString();
        Assert.Contains($"[{DeviceId}] X.509 certificate CN={DeviceId}, issued by CN=IIoT Device Issuing CA", output, StringComparison.Ordinal);
        Assert.Contains($"username: example-hub.azure-devices.net/{DeviceId}/?api-version=", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WithACertificateForAnotherDevice_FailsBeforeAnythingHappens()
    {
        var log = new StringWriter();
        var runner = new SimulationRunner(Options("tank-000002"), FixtureSource.Store, log);

        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(CancellationToken.None));

        Assert.Empty(log.ToString());
    }

    private TestClientOptions Options(string deviceId) => new()
    {
        Hub = "example-hub",
        DeviceId = deviceId,
        CertificateFile = _chainFile,
        KeyFile = _keyFile,
        MessageCount = 1,
        DryRun = true
    };
}
