using IIoT.TestClient.Configuration;
using IIoT.TestClient.Payloads;

namespace IIoT.TestClient.Tests.Configuration;

public class CommandLineTests
{
    private const string DeviceId = "tank-000123";
    private const string PlaceholderKey = "UGxhY2Vob2xkZXJLZXlOb3RBUmVhbFNlY3JldDEyMzQ1Njc4OTA=";

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    [Fact]
    public void Parse_Defaults_AreTheSafeOnes()
    {
        var options = Parse("--hub", "example-hub", "--device-id", DeviceId, "--device-key", PlaceholderKey);

        Assert.NotNull(options);
        Assert.Equal("example-hub.azure-devices.net", options!.HostName);
        Assert.Equal(1, options.DeviceCount);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Interval);
        Assert.Null(options.MessageCount);

        // Valid fixtures and mutation on by default: the out-of-the-box run is the one that should
        // flow all the way through the pipeline.
        Assert.Equal(TelemetryFixtures.ValidSelector, options.Fixture);
        Assert.True(options.Mutate);
        Assert.False(options.DryRun);
    }

    [Fact]
    public void Parse_FallsBackToTheEnvironment()
    {
        // The reason the fallback exists: a key passed as an argument lands in shell history.
        var environment = (string name) => name switch
        {
            CommandLine.HubVariable => "example-hub",
            CommandLine.DeviceIdVariable => DeviceId,
            CommandLine.DeviceKeyVariable => PlaceholderKey,
            _ => null
        };

        var options = CommandLine.Parse([], environment);

        Assert.NotNull(options);
        Assert.Equal("example-hub.azure-devices.net", options!.HostName);
        Assert.Equal(DeviceId, options.DeviceId);
        Assert.Equal(PlaceholderKey, options.DeviceKey);
    }

    [Fact]
    public void Parse_ArgumentsWinOverTheEnvironment()
    {
        var options = CommandLine.Parse(
            ["--hub", "from-argument", "--device-id", DeviceId, "--device-key", PlaceholderKey],
            name => name == CommandLine.HubVariable ? "from-environment" : null);

        Assert.Equal("from-argument.azure-devices.net", options!.HostName);
    }

    [Fact]
    public void Parse_Once_IsOneMessage()
    {
        var options = Parse("--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey, "--once");

        Assert.Equal(1, options!.MessageCount);
    }

    [Fact]
    public void Parse_NoMutate_TurnsMutationOff()
    {
        var options = Parse("--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey, "--no-mutate");

        Assert.False(options!.Mutate);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("valid")]
    [InlineData("invalid")]
    [InlineData("valid-buffered-backfill")]
    public void Parse_FixtureSelector_IsPassedThroughUnchanged(string selector)
    {
        var options = Parse(
            "--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey,
            "--fixture", selector);

        Assert.Equal(selector, options!.Fixture);
    }

    [Fact]
    public void Parse_Help_ReturnsNothingToRun()
    {
        Assert.Null(Parse("--help"));
        Assert.Null(Parse("-h"));
    }

    [Fact]
    public void Parse_WithoutAHub_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(
            () => Parse("--device-id", DeviceId, "--device-key", PlaceholderKey));

        Assert.Contains(CommandLine.HubVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithoutAnIdentity_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse("--hub", "h"));

        Assert.Contains("--devices", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AFleetFromASingleKey_Fails()
    {
        // One symmetric key authenticates exactly one identity, so --device-count above 1 without
        // a devices file would produce N connections all claiming the same device — which IoT Hub
        // resolves by dropping all but the newest.
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey,
            "--device-count", "5"));

        Assert.Contains("--devices", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--interval")]
    [InlineData("--count")]
    [InlineData("--device-count")]
    public void Parse_ANumericOptionWithoutAValue_Fails(string option)
    {
        Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey, option));
    }

    [Fact]
    public void Parse_ANonNumericCount_Fails()
    {
        Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey,
            "--count", "lots"));
    }

    [Fact]
    public void Parse_AnUnknownOption_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "--hub", "h", "--device-id", DeviceId, "--device-key", PlaceholderKey, "--turbo"));

        Assert.Contains("--turbo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_DocumentsEveryOption()
    {
        // The usage text is the only help a user of this tool gets at the terminal, so it must not
        // drift away from the parser.
        foreach (var option in new[]
        {
            "--hub", "--device-id", "--device-key", "--devices", "--device-count",
            "--interval", "--count", "--once", "--fixture", "--no-mutate",
            "--sas-lifetime", "--dry-run"
        })
        {
            Assert.Contains(option, CommandLine.Usage, StringComparison.Ordinal);
        }

        Assert.Contains("NEVER COMMIT", CommandLine.Usage, StringComparison.Ordinal);
    }

    private static TestClientOptions? Parse(params string[] args) =>
        CommandLine.Parse(args, NoEnvironment);
}
