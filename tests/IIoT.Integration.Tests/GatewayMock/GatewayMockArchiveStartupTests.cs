using IIoT.GatewayMock.Readings;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// <c>GatewayMock:Archive</c>: the container URI switches the archive on, and a setting that would
/// send the readings over plain HTTP, or reach the container with a SAS token, stops the mock from
/// starting. Split into validator and host level for the reason given on
/// <see cref="GatewayMockStartupTests"/>.
/// </summary>
public sealed class GatewayMockArchiveStartupTests : IDisposable
{
    private const string ContainerUri = "https://stiiotstaging.blob.core.windows.net/gateway-mock-readings";

    private readonly TestTokenIssuer _issuer = new();

    [Theory]
    [InlineData("GatewayMock:Archive:ContainerUri", "http://stiiotstaging.blob.core.windows.net/readings", "ContainerUri must be an absolute HTTPS URL")]
    [InlineData("GatewayMock:Archive:ContainerUri", "stiiotstaging.blob.core.windows.net/readings", "ContainerUri must be an absolute HTTPS URL")]
    [InlineData("GatewayMock:Archive:ContainerUri", "https://stiiotstaging.blob.core.windows.net/readings?sv=2026-02-06&sig=abc", "ContainerUri must have no query or fragment")]
    [InlineData("GatewayMock:Archive:ContainerUri", "https://stiiotstaging.blob.core.windows.net/", "ContainerUri must name exactly one container")]
    [InlineData("GatewayMock:Archive:ContainerUri", "https://stiiotstaging.blob.core.windows.net/readings/tank-000001", "ContainerUri must name exactly one container")]
    [InlineData("GatewayMock:Archive:Credential", "accountkey", "Credential must be managedidentity")]
    public void InvalidSetting_IsRejected(string setting, string value, string expected)
    {
        var settings = ArchiveSettings();
        settings[setting] = value;

        Assert.Contains(expected, Validate(settings), StringComparison.Ordinal);
    }

    /// <summary>Positive control: each rejection above is caused by the one setting it changes.</summary>
    [Theory]
    [InlineData("managedidentity")]
    [InlineData("ManagedIdentity")]
    [InlineData(null)]
    public void ValidConfiguration_IsAccepted(string? credential)
    {
        var settings = ArchiveSettings();
        settings["GatewayMock:Archive:Credential"] = credential;

        Assert.Null(Validate(settings));
    }

    /// <summary>Removing <c>ValidateOnStart</c> from the archive's registration makes this fail.</summary>
    [Fact]
    public async Task RejectedConfiguration_StopsStartup()
    {
        var settings = GatewayAndArchiveSettings();
        settings["GatewayMock:Archive:ContainerUri"] = "http://stiiotstaging.blob.core.windows.net/readings";

        await using var factory = new GatewayMockFactory(settings);

        Assert.NotNull(Record.Exception(() => factory.CreateClient()));
    }

    /// <summary>
    /// With the setting, the mock archives in Blob Storage, and starts without reaching it: nothing
    /// is read or written until a delivery or the dashboard needs it.
    /// </summary>
    [Fact]
    public async Task ContainerUri_SwitchesTheBlobArchiveOn_WithoutReachingItAtStartup()
    {
        await using var factory = new GatewayMockFactory(GatewayAndArchiveSettings());
        using var client = factory.CreateClient();

        Assert.IsType<BlobReadingArchive>(factory.Services.GetService<IReadingArchive>());
        Assert.True(factory.Services.GetRequiredService<ReadingHistory>().IsArchived);
    }

    /// <summary>Without it, memory only, as before: no archive, and nothing about storage to configure.</summary>
    [Fact]
    public async Task WithoutAContainerUri_TheMockHoldsWhatItAcceptsInMemoryOnly()
    {
        var settings = GatewayAndArchiveSettings();
        settings["GatewayMock:Archive:ContainerUri"] = "";

        await using var factory = new GatewayMockFactory(settings);
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetService<IReadingArchive>());
        Assert.False(factory.Services.GetRequiredService<ReadingHistory>().IsArchived);
    }

    public void Dispose() => _issuer.Dispose();

    private static Dictionary<string, string?> ArchiveSettings() => new()
    {
        ["GatewayMock:Archive:ContainerUri"] = ContainerUri,
        ["GatewayMock:Archive:Credential"] = "managedidentity"
    };

    private Dictionary<string, string?> GatewayAndArchiveSettings()
    {
        var settings = _issuer.GatewaySettings();
        foreach (var (key, value) in ArchiveSettings())
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>
    /// Binds the settings as the archive's registration does and runs its validator. Returns the
    /// failure message, or <c>null</c> when the configuration is accepted.
    /// </summary>
    private static string? Validate(Dictionary<string, string?> settings)
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(ReadingArchiveOptions.SectionName)
            .Get<ReadingArchiveOptions>() ?? new ReadingArchiveOptions();

        var result = new ReadingArchiveOptionsValidator().Validate(name: null, options);
        return result.Failed ? result.FailureMessage : null;
    }
}
