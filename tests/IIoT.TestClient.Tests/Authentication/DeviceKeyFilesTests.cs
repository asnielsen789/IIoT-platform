using System.Security.Cryptography;
using IIoT.DeviceCa.Certificates;
using IIoT.DeviceCa.Commands;
using IIoT.TestClient.Authentication;

namespace IIoT.TestClient.Tests.Authentication;

public sealed class DeviceKeyFilesTests : IDisposable
{
    private const string DeviceId = "tank-000001";

    private readonly string _directory = Directory.CreateTempSubdirectory("iiot-testclient-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Create_WritesTheKeyAndTheCsrUnderTheDeviceStem()
    {
        var target = Path.Combine(_directory, "new", "nested");

        var written = DeviceKeyFiles.Create(DeviceId, target);

        Assert.Equal(Path.Combine(target, "tank-000001.key.pem"), written.KeyPath);
        Assert.Equal(Path.Combine(target, "tank-000001.csr.pem"), written.CsrPath);

        var request = DeviceCertificateRequest.LoadPem(File.ReadAllText(written.CsrPath));
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(written.KeyPath));
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), request.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Theory]
    [InlineData(".key.pem")]
    [InlineData(".csr.pem")]
    public void Create_NeverOverwrites(string existingSuffix)
    {
        var existing = Path.Combine(_directory, DeviceId + existingSuffix);
        File.WriteAllText(existing, "an earlier file");

        Assert.Throws<IOException>(() => DeviceKeyFiles.Create(DeviceId, _directory));

        // Nothing written, nothing replaced: the old key, if that is what it was, is intact.
        Assert.Equal("an earlier file", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Create_OnUnix_LeavesTheKeyReadableByTheOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        var written = DeviceKeyFiles.Create(DeviceId, _directory);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(written.KeyPath));
    }

    [Theory]
    [InlineData("tank-000001")]
    [InlineData("site:7/a")]
    [InlineData("x$y'z(1)")]
    [InlineData("dev.ice_1")]
    public void FileStem_IsTheDeviceCasRule(string deviceId)
    {
        // The key and CSR, and the certificate and chain issue writes next to them, share one stem.
        Assert.Equal(IssueCommand.FileStem(deviceId), DeviceKeyFiles.FileStem(deviceId));
    }

    [Fact]
    public void DefaultDirectory_IsOutsideTheRepository()
    {
        var repository = RepositoryRoot();

        Assert.False(
            Path.GetFullPath(DeviceKeyFiles.DefaultDirectory).StartsWith(repository, StringComparison.OrdinalIgnoreCase),
            $"{DeviceKeyFiles.DefaultDirectory} is inside {repository}.");
        Assert.EndsWith(Path.Combine(".secrets", "iiot"), DeviceKeyFiles.DefaultDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void KeySuffix_IsWhatGitignoreGuards()
    {
        // The guard for a key written inside the repository by mistake.
        var patterns = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".gitignore")).Select(line => line.Trim());

        Assert.Contains("*" + DeviceKeyFiles.KeySuffix, patterns);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IIoT.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("No IIoT.slnx above the test output directory.");
    }
}
