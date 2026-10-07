namespace IIoT.TestClient.Payloads;

/// <summary>
/// Loads the contract's example payloads from the output directory and resolves the
/// <c>--fixture</c> selector.
/// </summary>
/// <remarks>
/// Fixtures are discovered, not listed, so an example added to <c>docs/schemas/examples/</c>
/// becomes sendable without a code change. The filename prefix states the expected outcome.
/// </remarks>
public sealed class TelemetryFixtures
{
    /// <summary>Selector: every fixture, valid and invalid.</summary>
    public const string AllSelector = "all";

    /// <summary>Selector: only the payloads the pipeline must accept.</summary>
    public const string ValidSelector = "valid";

    /// <summary>Selector: only the payloads the pipeline must reject.</summary>
    public const string InvalidSelector = "invalid";

    internal const string ValidPrefix = "valid-";
    internal const string InvalidPrefix = "invalid-";

    private const string Extension = ".json";

    private readonly List<TelemetryFixture> _fixtures;

    public TelemetryFixtures(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Root = root;
        _fixtures = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*" + Extension)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new TelemetryFixture(Path.GetFileName(path), File.ReadAllBytes(path)))
                .ToList()
            : [];
    }

    /// <summary>The directory the fixtures were loaded from.</summary>
    public string Root { get; }

    /// <summary>Every fixture found, ordered by file name.</summary>
    public IReadOnlyList<TelemetryFixture> All => _fixtures;

    /// <summary>The default fixture location: the copy next to the built assembly.</summary>
    public static TelemetryFixtures FromOutputDirectory() =>
        new(Path.Combine(AppContext.BaseDirectory, "Fixtures"));

    /// <summary>
    /// Resolves a <c>--fixture</c> selector: <c>all</c>, <c>valid</c>, <c>invalid</c>, or a single
    /// fixture name with or without the <c>.json</c> extension.
    /// </summary>
    /// <remarks>
    /// An unrecognised selector throws. It must never degrade to an empty selection: silently
    /// sending nothing looks exactly like a working run.
    /// </remarks>
    public IReadOnlyList<TelemetryFixture> Select(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var value = selector.Trim();

        var selected = value.ToLowerInvariant() switch
        {
            AllSelector => _fixtures,
            ValidSelector => Prefixed(ValidPrefix),
            InvalidSelector => Prefixed(InvalidPrefix),
            _ => Named(value)
        };

        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"No fixture matched '{selector}'. Available in '{Root}': {Describe()}. " +
                $"Use a name, or '{AllSelector}', '{ValidSelector}' or '{InvalidSelector}'.");
        }

        return selected;
    }

    private List<TelemetryFixture> Prefixed(string prefix) =>
        _fixtures.Where(f => f.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    private List<TelemetryFixture> Named(string name)
    {
        var fileName = name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + Extension;

        return _fixtures
            .Where(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string Describe() =>
        _fixtures.Count == 0 ? "(none)" : string.Join(", ", _fixtures.Select(f => f.Name));
}
