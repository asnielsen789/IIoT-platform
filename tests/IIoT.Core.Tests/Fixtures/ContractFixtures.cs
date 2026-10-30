using System.Text.Json;

namespace IIoT.Core.Tests.Fixtures;

/// <summary>
/// Access to the contract's example payloads in <c>docs/schemas/examples/</c>, which the test
/// project links into its output directory.
/// </summary>
/// <remarks>
/// The fixtures are discovered rather than listed, so a new example added to the contract
/// becomes a test case without anyone remembering to register it here. The filename prefix is
/// the expected outcome: <c>valid-</c> must pass validation, <c>invalid-</c> must not.
/// </remarks>
public static class ContractFixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<object[]> Valid() => Names("valid-*.json");

    public static IEnumerable<object[]> Invalid() => Names("invalid-*.json");

    /// <summary>
    /// The valid examples gateway contract v1 can carry: every measurement a <c>level</c> without a
    /// <c>sensor</c>. The others need v2 (ADR-011).
    /// </summary>
    public static IEnumerable<object[]> ValidForV1() => Valid().Where(name => FitsV1((string)name[0]));

    /// <summary>The valid examples with a measurement of another type, or one that names its sensor.</summary>
    public static IEnumerable<object[]> ValidNeedingV2() => Valid().Where(name => !FitsV1((string)name[0]));

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(Root, fileName));

    private static bool FitsV1(string fileName)
    {
        using var document = JsonDocument.Parse(Read(fileName));
        return document.RootElement.GetProperty("measurements").EnumerateArray()
            .All(measurement => measurement.GetProperty("type").GetString() == "level" && !measurement.TryGetProperty("sensor", out _));
    }

    private static IEnumerable<object[]> Names(string pattern) =>
        Directory.EnumerateFiles(Root, pattern)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new object[] { name! });
}
