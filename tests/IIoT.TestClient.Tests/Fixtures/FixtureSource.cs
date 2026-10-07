using IIoT.TestClient.Payloads;

namespace IIoT.TestClient.Tests.Fixtures;

/// <summary>
/// Shared access to the contract fixtures copied into this project's output directory.
/// </summary>
public static class FixtureSource
{
    /// <summary>The directory the fixtures are copied to.</summary>
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>A store rooted at the test project's own copy of the fixtures.</summary>
    public static TelemetryFixtures Store { get; } = new(Root);

    /// <summary>Theory data: every <c>valid-*</c> fixture name.</summary>
    public static IEnumerable<object[]> Valid() => Names(TelemetryFixtures.ValidSelector);

    /// <summary>Theory data: every <c>invalid-*</c> fixture name.</summary>
    public static IEnumerable<object[]> Invalid() => Names(TelemetryFixtures.InvalidSelector);

    /// <summary>Reads one fixture's bytes straight from disk, bypassing the client's loader.</summary>
    public static byte[] ReadBytes(string fileName) =>
        File.ReadAllBytes(Path.Combine(Root, fileName));

    private static IEnumerable<object[]> Names(string selector)
    {
        // A selector that matched nothing would throw; the guard test asserts that it does not.
        var fixtures = Directory.Exists(Root) ? Store.All : [];

        return fixtures
            .Where(f => f.Name.StartsWith(selector + "-", StringComparison.Ordinal))
            .Select(f => new object[] { f.Name });
    }
}
