using IIoT.TestClient.Configuration;

namespace IIoT.TestClient.Tests.Configuration;

/// <summary>
/// The certificate options and <c>create-csr</c>. The shared access key options keep their own
/// tests in <see cref="CommandLineTests"/>, unchanged.
/// </summary>
public class CertificateCommandLineTests
{
    private const string DeviceId = "tank-000001";
    private const string PlaceholderKey = "UGxhY2Vob2xkZXJLZXlOb3RBUmVhbFNlY3JldDEyMzQ1Njc4OTA=";
    private const string DefaultDirectory = "/home/operator/.secrets/iiot";

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    [Fact]
    public void Parse_CertAndKey_SelectCertificateAuthentication()
    {
        var options = Parse("--hub", "h", "--device-id", DeviceId, "--cert", "d.chain.pem", "--key", "d.key.pem");

        Assert.NotNull(options);
        Assert.True(options!.UsesCertificate);
        Assert.Equal("d.chain.pem", options.CertificateFile);
        Assert.Equal("d.key.pem", options.KeyFile);
        Assert.Null(options.DeviceKey);
        Assert.Null(options.DevicesFile);
    }

    [Fact]
    public void Parse_WithoutCert_IsAKeyRunAsBefore()
    {
        var options = Parse("--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey);

        Assert.False(options!.UsesCertificate);
        Assert.Null(options.CertificateFile);
        Assert.Equal(TimeSpan.FromHours(1), options.SasLifetime);
    }

    [Fact]
    public void Parse_Cert_IgnoresTheKeyFallbacksInTheEnvironment()
    {
        // A DEVICE_KEY or DEVICES_FILE left over from a key session must not collide with --cert.
        var environment = (string name) => name switch
        {
            CommandLine.HubVariable => "example-hub",
            CommandLine.DeviceIdVariable => DeviceId,
            CommandLine.DeviceKeyVariable => PlaceholderKey,
            CommandLine.DevicesFileVariable => "fleet-devices.json",
            _ => null
        };

        var options = CommandLine.Parse(["--cert", "d.chain.pem", "--key", "d.key.pem"], environment);

        Assert.Equal(DeviceId, options!.DeviceId);
        Assert.Null(options.DeviceKey);
        Assert.Null(options.DevicesFile);
        Assert.True(options.UsesCertificate);
    }

    [Fact]
    public void Parse_CertAndDeviceKey_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey,
            "--cert", "d.chain.pem", "--key", "d.key.pem"));

        Assert.Contains("--device-key", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cert", "d.chain.pem")]
    [InlineData("--key", "d.key.pem")]
    public void Parse_CertOrKeyAlone_Fails(string option, string value)
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, option, value));

        Assert.Contains("--cert and --key go together", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CertWithoutDeviceId_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--cert", "d.chain.pem", "--key", "d.key.pem"));

        Assert.Contains("--device-id", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--devices", "fleet-devices.json")]
    [InlineData("--device-count", "2")]
    public void Parse_CertForAFleet_Fails(string option, string value)
    {
        // Certificates are single-device in this client; see TestClientOptions.ValidateCertificate.
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--cert", "d.chain.pem", "--key", "d.key.pem", option, value));

        Assert.Contains("one device", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CertWithSasLifetime_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--cert", "d.chain.pem", "--key", "d.key.pem",
            "--sas-lifetime", "30"));

        Assert.Contains("--sas-lifetime", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCreateCsr_DefaultsToTheDirectoryOutsideTheRepository()
    {
        var options = CommandLine.ParseCreateCsr(["create-csr", "--device-id", DeviceId], NoEnvironment, DefaultDirectory);

        Assert.Equal(new CreateCsrOptions(DeviceId, DefaultDirectory), options);
    }

    [Fact]
    public void ParseCreateCsr_TakesOutAndTheDeviceIdFallback()
    {
        var options = CommandLine.ParseCreateCsr(
            ["create-csr", "--out", "/elsewhere"],
            name => name == CommandLine.DeviceIdVariable ? DeviceId : null,
            DefaultDirectory);

        Assert.Equal(new CreateCsrOptions(DeviceId, "/elsewhere"), options);
    }

    [Fact]
    public void ParseCreateCsr_WithoutADeviceId_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(
            () => CommandLine.ParseCreateCsr(["create-csr"], NoEnvironment, DefaultDirectory));

        Assert.Contains("--device-id", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--hub", "h")]
    [InlineData("--cert", "d.chain.pem")]
    [InlineData("--force", "x")]
    public void ParseCreateCsr_ARunOption_Fails(string option, string value)
    {
        var exception = Assert.Throws<CommandLineException>(() => CommandLine.ParseCreateCsr(
            ["create-csr", "--device-id", DeviceId, option, value], NoEnvironment, DefaultDirectory));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCreateCsr_Help_ReturnsNothingToRun()
    {
        Assert.Null(CommandLine.ParseCreateCsr(["create-csr", "--help"], NoEnvironment, DefaultDirectory));
    }

    [Fact]
    public void IsCreateCsr_OnlyForTheFirstArgument()
    {
        Assert.True(CommandLine.IsCreateCsr(["create-csr", "--device-id", DeviceId]));
        Assert.False(CommandLine.IsCreateCsr(["--hub", "h", "create-csr"]));
        Assert.False(CommandLine.IsCreateCsr([]));
    }

    [Fact]
    public void Usage_DocumentsTheCertificateOptions()
    {
        foreach (var text in new[] { "--cert", "--key", "create-csr", "--out", "NEVER COMMIT OR SHARE IT" })
            Assert.Contains(text, CommandLine.Usage, StringComparison.Ordinal);
    }

    private static TestClientOptions? Parse(params string[] args) =>
        CommandLine.Parse(args, NoEnvironment);
}
