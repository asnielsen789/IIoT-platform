using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// Validates a request body against one version of the gateway contract:
/// <c>docs/schemas/gateway/readings-v1.schema.json</c> or <c>readings-v2.schema.json</c> (ADR-011),
/// each linked into this assembly as an embedded resource.
/// </summary>
internal sealed class ReadingsSchema
{
    private const int MaxReportedErrors = 5;

    public static readonly ReadingsSchema V1 = new(1, "IIoT.GatewayMock.Schemas.readings-v1.schema.json");

    public static readonly ReadingsSchema V2 = new(2, "IIoT.GatewayMock.Schemas.readings-v2.schema.json");

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,

        // "format": "date-time" is only an annotation unless this is set. The contract means
        // it as a constraint.
        RequireFormatValidation = true,

        // Report the failing leaf, not every parent that merely restates it.
        IncludeApplicatorErrors = false
    };

    private readonly string _resourceName;
    private readonly Lazy<JsonSchema> _schema;

    private ReadingsSchema(int version, string resourceName)
    {
        Version = version;
        _resourceName = resourceName;
        _schema = new Lazy<JsonSchema>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The contract version: 1 for <c>/v1/readings</c>, 2 for <c>/v2/readings</c>.</summary>
    public int Version { get; }

    /// <summary>Returns <c>null</c> if <paramref name="body"/> is valid, otherwise why it is not.</summary>
    public string? Validate(JsonElement body)
    {
        var evaluation = _schema.Value.Evaluate(body, Options);
        return evaluation.IsValid ? null : Describe(evaluation);
    }

    private JsonSchema Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(_resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{_resourceName}' was not found. It is linked in from docs/schemas/gateway/ " +
                "by IIoT.GatewayMock.csproj; check that the EmbeddedResource item and its LogicalName still match.");

        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private string Describe(EvaluationResults evaluation)
    {
        var errors = Flatten(evaluation)
            .Where(detail => detail.Errors is { Count: > 0 } && !IsCondition(detail))
            .SelectMany(detail => detail.Errors!.Select(error =>
            {
                var location = detail.InstanceLocation.ToString();
                return string.IsNullOrEmpty(location) ? error.Value : $"{location}: {error.Value}";
            }))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (errors.Count == 0)
        {
            return $"Body does not match the readings v{Version} schema";
        }

        var reported = string.Join("; ", errors.Take(MaxReportedErrors));
        return errors.Count > MaxReportedErrors
            ? $"{reported}; and {errors.Count - MaxReportedErrors} more"
            : reported;
    }

    /// <summary>
    /// Whether the result belongs to an <c>if</c>. v2's per-kind rules (ADR-011) ask every reading
    /// "is this energy?", and a "no" only means the rule does not apply; the <c>then</c> that does
    /// apply reports the fault. The same rule as the platform's validator.
    /// </summary>
    private static bool IsCondition(EvaluationResults detail)
    {
        var segments = detail.EvaluationPath.ToString().Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i] == "if" && segments[i - 1] != "properties")
            {
                return true;
            }
        }

        return false;
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
