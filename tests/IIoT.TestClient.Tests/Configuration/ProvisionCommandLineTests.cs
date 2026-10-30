using IIoT.TestClient.Configuration;
using IIoT.TestClient.Payloads;
using IIoT.TestClient.Provisioning;

namespace IIoT.TestClient.Tests.Configuration;

/// <summary>The <c>provision</c> command line.</summary>
public class ProvisionCommandLineTests
{
    private const string IdScope = "0ne00000000";

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    [Fact]
    public void IsProvision_OnlyAsTheFirstArgument()
    {
        Assert.True(CommandLine.IsProvision(["provision", "--id-scope", IdScope]));
        Assert.False(CommandLine.IsProvision(["--hub", "h", "provision"]));
        Assert.False(CommandLine.IsProvision([]));
    }

    [Fact]
    public void ParseProvision_TheMinimum_SendsAValidMessageThroughTheGlobalEndpoint()
    {
        var options = Parse("provision", "--id-scope", IdScope, "--cert", "d.chain.pem", "--key", "d.key.pem");

        Assert.Equal(new ProvisionOptions(IdScope, "d.chain.pem", "d.key.pem"), options);
        Assert.Equal(DpsEndpoint.GlobalHostName, options!.DpsHost);
        Assert.True(options.Send);
        Assert.Equal(TelemetryFixtures.ValidSelector, options.Fixture);
    }

    [Fact]
    public void ParseProvision_EveryOption()
    {
        var options = Parse(
            "provision", "--id-scope", IdScope, "--cert", "d.chain.pem", "--key", "d.key.pem",
            "--dps-host", "dps.example.net", "--fixture", "valid-buffered-backfill");

        Assert.Equal("dps.example.net", options!.DpsHost);
        Assert.Equal("valid-buffered-backfill", options.Fixture);
    }

    [Fact]
    public void ParseProvision_NoSend_StopsAtTheAssignment()
    {
        var options = Parse("provision", "--id-scope", IdScope, "--cert", "c", "--key", "k", "--no-send");

        Assert.False(options!.Send);
    }

    [Fact]
    public void ParseProvision_TheIdScopeFallsBackToTheEnvironment()
    {
        var options = CommandLine.ParseProvision(
            ["provision", "--cert", "c", "--key", "k"],
            name => name == CommandLine.IdScopeVariable ? IdScope : null);

        Assert.Equal(IdScope, options!.IdScope);
    }

    [Fact]
    public void ParseProvision_WithoutAnIdScope_Fails()
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse("provision", "--cert", "c", "--key", "k"));

        Assert.Contains(CommandLine.IdScopeVariable, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cert", "c")]
    [InlineData("--key", "k")]
    public void ParseProvision_WithoutBothCertAndKey_Fails(string option, string value)
    {
        var exception = Assert.Throws<CommandLineException>(() => Parse("provision", "--id-scope", IdScope, option, value));

        Assert.Contains("--cert and --key", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--hub", "h")]
    [InlineData("--device-id", "tank-000003")]
    [InlineData("--device-key", "x")]
    public void ParseProvision_WhatDpsAndTheCertificateDecide_IsNotAnOption(string option, string value)
    {
        // The hub comes from DPS and the device ID from the certificate's CN.
        var exception = Assert.Throws<CommandLineException>(() => Parse(
            "provision", "--id-scope", IdScope, "--cert", "c", "--key", "k", option, value));

        Assert.Contains($"'{option}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseProvision_AFixtureWithNoSend_Fails()
    {
        Assert.Throws<CommandLineException>(() => Parse(
            "provision", "--id-scope", IdScope, "--cert", "c", "--key", "k", "--no-send", "--fixture", "valid"));
    }

    [Fact]
    public void ParseProvision_Help_RunsNothing()
    {
        Assert.Null(Parse("provision", "--help"));
    }

    [Fact]
    public void ParseProvision_AnotherCommand_Fails()
    {
        Assert.Throws<CommandLineException>(() => Parse("create-csr", "--device-id", "tank-000003"));
    }

    private static ProvisionOptions? Parse(params string[] args) => CommandLine.ParseProvision(args, NoEnvironment);
}
