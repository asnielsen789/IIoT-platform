using System.Text.Json;
using Json.Schema;

namespace IIoT.Core.Tests.Forwarders;

/// <summary>
/// The published gateway schema, <c>docs/schemas/gateway/readings-v1.schema.json</c>, as linked
/// into this project's output — the same file the gateway mock enforces.
/// </summary>
/// <remarks>Loaded once: a schema with an <c>$id</c> is registered globally when it is built.</remarks>
internal static class GatewaySchema
{
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gateway", "readings-v1.schema.json");

    private static readonly Lazy<JsonSchema> LazySchema =
        new(() => JsonSchema.FromText(File.ReadAllText(FilePath)), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    };

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
