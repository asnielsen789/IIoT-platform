using System.Text.Json;
using Json.Schema;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// The published gateway schema v2, <c>docs/schemas/gateway/readings-v2.schema.json</c>, as linked
/// into this project's output (ADR-011).
/// </summary>
/// <remarks>Loaded once: a schema with an <c>$id</c> is registered globally when it is built.</remarks>
internal static class GatewayV2Schema
{
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gateway", "readings-v2.schema.json");

    public static string ExamplesRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GatewayV2");

    private static readonly Lazy<JsonSchema> LazySchema =
        new(() => JsonSchema.FromText(File.ReadAllText(FilePath)), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    };

    /// <summary>Theory data: the v2 examples whose name starts with <paramref name="prefix"/>.</summary>
    public static IEnumerable<object[]> Examples(string prefix) =>
        Directory.Exists(ExamplesRoot)
            ? Directory.EnumerateFiles(ExamplesRoot, prefix + "-*.json")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new object[] { name! })
            : [];

    public static IEnumerable<object[]> Valid() => Examples("valid");

    public static IEnumerable<object[]> Invalid() => Examples("invalid");

    public static byte[] Read(string fileName) => File.ReadAllBytes(Path.Combine(ExamplesRoot, fileName));

    /// <summary>Every error the schema reports for <paramref name="utf8Json"/>; empty when valid.</summary>
    public static IReadOnlyList<string> Errors(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var results = LazySchema.Value.Evaluate(document.RootElement, Options);

        return results.IsValid
            ? []
            : Flatten(results)
                .Where(detail => detail.Errors is { Count: > 0 })
                .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
                .ToList();
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results) =>
        new[] { results }.Concat((results.Details ?? []).SelectMany(Flatten));
}
