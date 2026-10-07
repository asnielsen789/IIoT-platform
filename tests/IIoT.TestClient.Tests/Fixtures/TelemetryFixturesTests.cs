using IIoT.TestClient.Payloads;
using IIoT.TestClient.Tests.Fixtures;

namespace IIoT.TestClient.Tests;

public class TelemetryFixturesTests
{
    /// <summary>
    /// Guards every theory in this project. A theory whose data source silently returns nothing
    /// looks like a suite that passes, which is exactly what happens when the fixtures stop being
    /// copied to the output directory.
    /// </summary>
    [Fact]
    public void Fixtures_AreCopiedToTheOutputDirectory()
    {
        Assert.True(
            Directory.Exists(FixtureSource.Root),
            $"the fixture directory '{FixtureSource.Root}' does not exist");

        Assert.True(
            FixtureSource.Valid().Count() >= 4,
            "valid fixtures were not copied to the output directory");

        Assert.True(
            FixtureSource.Invalid().Count() >= 4,
            "invalid fixtures were not copied to the output directory");
    }

    [Fact]
    public void Select_Invalid_ReturnsTheInvalidFixtures()
    {
        var selected = FixtureSource.Store.Select(TelemetryFixtures.InvalidSelector);

        // The invalid fixtures must be sendable: proving that the pipeline rejects a bad message
        // with a logged reason is an acceptance criterion of its own. A client that quietly
        // filtered them out could not demonstrate it.
        Assert.NotEmpty(selected);
        Assert.All(selected, fixture =>
        {
            Assert.StartsWith("invalid-", fixture.Name, StringComparison.Ordinal);
            Assert.False(fixture.IsExpectedValid);
        });

        Assert.Contains("invalid-empty-measurements.json", selected.Select(f => f.Name));
        Assert.Contains("invalid-missing-firmware.json", selected.Select(f => f.Name));
        Assert.Contains("invalid-negative-level.json", selected.Select(f => f.Name));
        Assert.Contains("invalid-wrong-unit.json", selected.Select(f => f.Name));
    }

    [Fact]
    public void Select_Valid_ReturnsOnlyTheValidFixtures()
    {
        var selected = FixtureSource.Store.Select(TelemetryFixtures.ValidSelector);

        Assert.NotEmpty(selected);
        Assert.All(selected, fixture => Assert.True(fixture.IsExpectedValid));
    }

    [Fact]
    public void Select_All_ReturnsValidAndInvalidTogether()
    {
        var selected = FixtureSource.Store.Select(TelemetryFixtures.AllSelector);

        Assert.Equal(FixtureSource.Store.All.Count, selected.Count);
        Assert.Contains(selected, fixture => fixture.IsExpectedValid);
        Assert.Contains(selected, fixture => !fixture.IsExpectedValid);
    }

    [Theory]
    [InlineData("valid-buffered-backfill")]
    [InlineData("valid-buffered-backfill.json")]
    [InlineData("VALID-BUFFERED-BACKFILL.JSON")]
    public void Select_ByName_ResolvesWithOrWithoutTheExtension(string selector)
    {
        var selected = FixtureSource.Store.Select(selector);

        var fixture = Assert.Single(selected);
        Assert.Equal("valid-buffered-backfill.json", fixture.Name);
    }

    [Fact]
    public void Select_UnknownName_Throws()
    {
        // Never degrade to an empty selection: a run that sends nothing looks like a run that
        // worked.
        var exception = Assert.Throws<InvalidOperationException>(
            () => FixtureSource.Store.Select("valid-does-not-exist"));

        Assert.Contains("valid-minimal.json", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(FixtureSource.Valid), MemberType = typeof(FixtureSource))]
    public void Load_ReadsTheFileBytesUnaltered(string fileName)
    {
        var fixture = Assert.Single(FixtureSource.Store.Select(fileName));

        Assert.Equal(FixtureSource.ReadBytes(fileName), fixture.Payload);
    }
}
