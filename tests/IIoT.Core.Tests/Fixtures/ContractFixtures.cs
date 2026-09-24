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

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(Root, fileName));

    private static IEnumerable<object[]> Names(string pattern) =>
        Directory.EnumerateFiles(Root, pattern)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new object[] { name! });
}
