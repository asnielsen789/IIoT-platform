using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// Validates a request body against <c>docs/schemas/gateway/readings-v1.schema.json</c>, which is
/// linked into this assembly as an embedded resource.
/// </summary>
internal static class ReadingsSchema
{
    private const string ResourceName = "IIoT.GatewayMock.Schemas.readings-v1.schema.json";
    private const int MaxReportedErrors = 5;

    private static readonly Lazy<JsonSchema> LazySchema = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,

        // "format": "date-time" is only an annotation unless this is set. The contract means
        // it as a constraint.
        RequireFormatValidation = true,

        // Report the failing leaf, not every parent that merely restates it.
        IncludeApplicatorErrors = false
    };

    /// <summary>Returns <c>null</c> if <paramref name="body"/> is valid, otherwise why it is not.</summary>
    public static string? Validate(JsonElement body)
    {
        var evaluation = LazySchema.Value.Evaluate(body, Options);
        return evaluation.IsValid ? null : Describe(evaluation);
    }

    private static JsonSchema Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{ResourceName}' was not found. It is linked in from docs/schemas/gateway/ " +
                "by IIoT.GatewayMock.csproj; check that the EmbeddedResource item and its LogicalName still match.");

        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static string Describe(EvaluationResults evaluation)
    {
        var errors = Flatten(evaluation)
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error =>
            {
                var location = detail.InstanceLocation.ToString();
                return string.IsNullOrEmpty(location) ? error.Value : $"{location}: {error.Value}";
            }))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (errors.Count == 0)
        {
            return "Body does not match the readings v1 schema";
        }

        var reported = string.Join("; ", errors.Take(MaxReportedErrors));
        return errors.Count > MaxReportedErrors
            ? $"{reported}; and {errors.Count - MaxReportedErrors} more"
            : reported;
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (var nested in (results.Details ?? []).SelectMany(Flatten))
        {
            yield return nested;
        }
    }
}
