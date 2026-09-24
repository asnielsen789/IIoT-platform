using System.Text.Json;
using IIoT.Core.Models;
using IIoT.Core.Serialization;
using Json.Schema;

namespace IIoT.Core.Validation;

/// <summary>
/// Validates a raw telemetry payload against the v1 contract and, if it passes, deserialises it.
/// </summary>
/// <remarks>
/// Validation comes first and deserialisation second, deliberately: the schema is the contract,
/// so a payload is judged against the published document rather than against whatever the C#
/// models happen to accept.
/// </remarks>
public static class TelemetryValidator
{
    private const string SchemaVersionProperty = "schemaVersion";
    private const int MaxReportedErrors = 5;

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,

        // Without this, "format": "date-time" is an annotation that is collected and ignored,
        // which is the JSON Schema default. The contract means it as a constraint.
        RequireFormatValidation = true,

        // Keeps the leaf error ("/measurements/0/value: -12.0 should be at least 0") and drops
        // the chain of parent applicators that merely restate that something below them failed.
        // The reason ends up in a log line, so it has to be readable.
        IncludeApplicatorErrors = false
    };

    /// <summary>Validates <paramref name="payload"/> and deserialises it on success.</summary>
    public static TelemetryValidationResult Validate(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return TelemetryValidationResult.Invalid("Payload is empty");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            return TelemetryValidationResult.Invalid($"Payload is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            var versionError = CheckSchemaVersion(root);
            if (versionError is not null)
                return TelemetryValidationResult.Invalid(versionError);

            var evaluation = TelemetrySchema.Instance.Evaluate(root, Options);
            if (!evaluation.IsValid)
                return TelemetryValidationResult.Invalid(Describe(evaluation));

            TelemetryMessage? message;
            try
            {
                message = root.Deserialize<TelemetryMessage>(TelemetryJson.Options);
            }
            catch (JsonException ex)
            {
                // The payload satisfied the schema but not the models, which means the two have
                // drifted apart. That is a bug here, not a bad message from the device.
                return TelemetryValidationResult.Invalid(
                    $"Payload passed schema validation but could not be deserialised: {ex.Message}");
            }

            return message is null
                ? TelemetryValidationResult.Invalid("Payload deserialised to null")
                : TelemetryValidationResult.Valid(message);
        }
    }

    /// <summary>
    /// Checked before the schema so an unrecognised version produces a specific, greppable
    /// reason rather than a generic <c>const</c> violation. Section 6 of the contract requires
    /// an unknown <c>schemaVersion</c> to be rejected explicitly and never interpreted as v1.
    /// </summary>
    private static string? CheckSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(SchemaVersionProperty, out var versionElement))
        {
            // Absent or not an object at all — the schema reports that better than this can.
            return null;
        }

        if (versionElement.ValueKind == JsonValueKind.Number &&
            versionElement.TryGetInt32(out var version) &&
            version == TelemetrySchema.SupportedVersion)
        {
            return null;
        }

        return $"Unsupported {SchemaVersionProperty} '{versionElement.GetRawText()}'; " +
               $"this build understands version {TelemetrySchema.SupportedVersion}";
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
            return "Payload does not match the v1 schema";

        var reported = string.Join("; ", errors.Take(MaxReportedErrors));
        return errors.Count > MaxReportedErrors
            ? $"{reported}; and {errors.Count - MaxReportedErrors} more"
            : reported;
    }

    /// <summary>
    /// Walks the result tree. <c>OutputFormat.List</c> flattens most of it, but a nested detail
    /// can still carry the only useful message, so both levels are collected.
    /// </summary>
    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (var nested in (results.Details ?? []).SelectMany(Flatten))
        {
            yield return nested;
        }
    }
}
