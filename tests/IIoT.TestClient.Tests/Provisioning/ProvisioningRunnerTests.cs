using IIoT.TestClient.Authentication;
using IIoT.TestClient.Configuration;
using IIoT.TestClient.Provisioning;
using IIoT.TestClient.Tests.Fixtures;
using IIoT.TestClient.Tests.TestDoubles;

namespace IIoT.TestClient.Tests.Provisioning;

/// <summary>
/// What <c>provision</c> checks before it asks DPS anything. Each case fails before a connection,
/// so none of them touches the network.
/// </summary>
public sealed class ProvisioningRunnerTests : IDisposable
{
    private const string DeviceId = "tank-000003";
    private const string IdScope = "0ne00000000";

    private readonly TestPki _pki = new(DateTimeOffset.UtcNow);
    private readonly string _directory = Directory.CreateTempSubdirectory("iiot-testclient-").FullName;
    private readonly string _chainFile;
    private readonly string _keyFile;

    public ProvisioningRunnerTests()
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
    public async Task Run_AnUnknownFixture_FailsBeforeTheRegistration()
    {
        var log = new StringWriter();
        var runner = new ProvisioningRunner(Options() with { Fixture = "valid-no-such-fixture" }, FixtureSource.Store, log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(CancellationToken.None));
        Assert.DoesNotContain("registering", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AKeyThatIsNotTheCertificates_FailsBeforeTheRegistration()
    {
        var otherKey = Path.Combine(_directory, "other.key.pem");
        File.WriteAllText(otherKey, DeviceCsr.Create(DeviceId).PrivateKeyPem);
        var log = new StringWriter();
        var runner = new ProvisioningRunner(Options() with { KeyFile = otherKey }, FixtureSource.Store, log);

        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(CancellationToken.None));
        Assert.DoesNotContain("registering", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AMissingCertificate_FailsBeforeTheRegistration()
    {
        var runner = new ProvisioningRunner(
            Options() with { CertificateFile = Path.Combine(_directory, "missing.pem") }, FixtureSource.Store, TextWriter.Null);

        await Assert.ThrowsAsync<FileNotFoundException>(() => runner.RunAsync(CancellationToken.None));
    }

    private ProvisionOptions Options() => new(IdScope, _chainFile, _keyFile);
}
