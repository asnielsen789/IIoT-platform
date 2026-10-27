using IIoT.DeviceCa.Configuration;
using IIoT.DeviceCa.IotHub;
using IIoT.DeviceCa.KeyVault;

namespace IIoT.DeviceCa.Tests.Configuration;

public class CommandLineTests
{
    [Fact]
    public void Parse_Init()
    {
        Assert.Equal(new InitCommandLine("kv-example", Force: false), CommandLine.Parse(["init", "--vault", "kv-example"]));
        Assert.Equal(new InitCommandLine("kv-example", Force: true), CommandLine.Parse(["init", "--vault", "kv-example", "--force"]));
    }

    [Fact]
    public void Parse_Issue_DefaultsToTheCurrentDirectory()
    {
        var command = CommandLine.Parse(
            ["issue", "--vault", "kv", "--hub", "hub", "--csr", "tank.csr.pem", "--device-id", "tank-000002"]);

        Assert.Equal(new IssueCommandLine("kv", "hub", "tank.csr.pem", "tank-000002", "."), command);
    }

    [Fact]
    public void Parse_Issue_WithAnOutputDirectory()
    {
        var command = Assert.IsType<IssueCommandLine>(CommandLine.Parse(
            ["issue", "--vault", "kv", "--hub", "hub", "--csr", "c", "--device-id", "d", "--out", "certs"]));

        Assert.Equal("certs", command.OutputDirectory);
    }

    [Fact]
    public void Parse_CaCertificate()
    {
        Assert.Equal(new CaCertificateCommandLine("kv", null), CommandLine.Parse(["ca-certificate", "--vault", "kv"]));
        Assert.Equal(
            new CaCertificateCommandLine("kv", "issuing.pem"),
            CommandLine.Parse(["ca-certificate", "--vault", "kv", "--out", "issuing.pem"]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_Help_ReturnsNothingToRun(string option)
    {
        Assert.Null(CommandLine.Parse([option]));
        Assert.Null(CommandLine.Parse(["init", option]));
    }

    [Fact]
    public void Parse_Issue_WithNoRegister_HasNoHub()
    {
        var command = CommandLine.Parse(
            ["issue", "--vault", "kv", "--no-register", "--csr", "tank.csr.pem", "--device-id", "tank-000003"]);

        Assert.Equal(new IssueCommandLine("kv", null, "tank.csr.pem", "tank-000003", "."), command);
    }

    [Fact]
    public void Parse_Issue_WithHubAndNoRegister_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => CommandLine.Parse(
            ["issue", "--vault", "kv", "--hub", "h", "--no-register", "--csr", "c", "--device-id", "d"]));

        Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "No command")]
    [InlineData(new[] { "sign" }, "Unknown command 'sign'")]
    [InlineData(new[] { "init" }, "--vault")]
    [InlineData(new[] { "issue", "--vault", "kv", "--csr", "c", "--device-id", "d" }, "--hub")]
    [InlineData(new[] { "issue", "--vault", "kv", "--hub", "h", "--device-id", "d" }, "--csr")]
    [InlineData(new[] { "issue", "--vault", "kv", "--hub", "h", "--csr", "c" }, "--device-id")]
    [InlineData(new[] { "init", "--vault" }, "needs a value")]
    [InlineData(new[] { "init", "--vault", "kv", "--turbo" }, "--turbo")]
    public void Parse_AnIncompleteCommand_Fails(string[] args, string expected)
    {
        var exception = Assert.Throws<CommandLineException>(() => CommandLine.Parse(args));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "issue", "--vault", "kv", "--hub", "h", "--csr", "c", "--device-id", "d", "--force" }, "--force")]
    [InlineData(new[] { "init", "--vault", "kv", "--hub", "h" }, "--hub")]
    [InlineData(new[] { "init", "--vault", "kv", "--out", "x" }, "--out")]
    [InlineData(new[] { "ca-certificate", "--vault", "kv", "--force" }, "--force")]
    [InlineData(new[] { "ca-certificate", "--vault", "kv", "--device-id", "d" }, "--device-id")]
    [InlineData(new[] { "init", "--vault", "kv", "--no-register" }, "--no-register")]
    [InlineData(new[] { "ca-certificate", "--vault", "kv", "--no-register" }, "--no-register")]
    public void Parse_AnOptionTheCommandDoesNotUse_Fails(string[] args, string option)
    {
        // An operator who passes --force to issue expects it to do something; silently ignoring it
        // would be worse than refusing.
        var exception = Assert.Throws<CommandLineException>(() => CommandLine.Parse(args));

        Assert.Contains($"does not take {option}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_DocumentsEveryCommandAndOption()
    {
        foreach (var text in new[]
        {
            "init", "issue", "ca-certificate", "--vault", "--hub", "--csr", "--device-id", "--out", "--force", "--no-register",
            "device-ca-root-certificate", "device-ca-issuing-certificate"
        })
        {
            Assert.Contains(text, CommandLine.Usage, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("kv-iiot-staging", "https://kv-iiot-staging.vault.azure.net/")]
    [InlineData("https://kv-example.vault.azure.cn/", "https://kv-example.vault.azure.cn/")]
    public void VaultUri_ABareNameGainsThePublicCloudSuffix(string value, string expected)
    {
        Assert.Equal(new Uri(expected), KeyVaultCa.ResolveUri(value));
    }

    [Theory]
    [InlineData("iot-example", "iot-example.azure-devices.net")]
    [InlineData("iot-example.azure-devices.cn", "iot-example.azure-devices.cn")]
    public void HubHostName_ABareNameGainsThePublicCloudSuffix(string value, string expected)
    {
        Assert.Equal(expected, IotHubDeviceRegistry.ResolveHostName(value));
    }
}
