using System.Text.Json;
using IIoT.Core.Models;
using IIoT.Core.Serialization;
using Json.Schema;

namespace IIoT.Core.Validation;

/// <summary>
/// Validates a raw telemetry payload against the v1 contract and, if it passes, deserialises it.
/// </summary>
/// <remarks>
/// <para>
/// Validation comes first and deserialisation second, deliberately: the schema is the contract,
/// so a payload is judged against the published document rather than against whatever the C#
/// models happen to accept.
/// </para>
/// <para>
/// Every payload ends in exactly one of two results, and never in an exception: valid and
/// deserialised, or rejected with a reason that names the field. A schema-valid payload is
/// deserialised by <see cref="TelemetryJson.Options"/>, which reads everything the schema accepts
/// or refuses a value it cannot hold with the reason why.
/// </para>
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

    /// <summary>
    /// A repeated member name is refused at parse time. JSON Schema leaves its meaning undefined
    /// (2020-12 core, section 4.2.1), and in practice the evaluator checked the first value while
    /// the deserialiser kept the last: <c>"value": 1432.5, "value": -50000</c> passed
    /// <c>"minimum": 0</c> and was forwarded as -50000.
    /// </summary>
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>Validates <paramref name="payload"/> and deserialises it on success.</summary>
    public static TelemetryValidationResult Validate(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return TelemetryValidationResult.Invalid("Payload is empty");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return TelemetryValidationResult.Invalid($"Payload is not valid JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            // A string holding an unpaired surrogate cannot be turned into UTF-8 to be parsed. A
            // body decoded from the wire never holds one, since invalid UTF-8 decodes to U+FFFD,
            // but this method takes any string.
            return TelemetryValidationResult.Invalid($"Payload is not valid text: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            var versionError = CheckSchemaVersion(root);
            if (versionError is not null)
                return TelemetryValidationResult.Invalid(versionError);

            // Resolved outside the try below: a schema that fails to load is a broken build, and
            // must not be reported as a bad message from every device.
            var schema = TelemetrySchema.Instance;

            EvaluationResults evaluation;
            try
            {
                evaluation = schema.Evaluate(root, Options);
            }
            catch (InvalidOperationException ex)
            {
                // A string holding an escaped lone surrogate, such as "\uD800", is well-formed JSON
                // (RFC 8259 section 8.2) but no string at all to .NET, so the evaluator throws when
                // it reads it to check a length or an enum. There is nothing to validate it against.
                return TelemetryValidationResult.Invalid($"Payload could not be evaluated against the v1 schema: {ex.Message}");
            }

            if (!evaluation.IsValid)
                return TelemetryValidationResult.Invalid(Describe(evaluation));

            TelemetryMessage? message;
            try
            {
                message = root.Deserialize<TelemetryMessage>(TelemetryJson.Options);
            }
            catch (RejectedValueException ex)
            {
                // Valid against the schema, but a value the platform cannot hold — see TelemetryJson.
                // Reported like a schema error: the field, then the reason.
                return TelemetryValidationResult.Invalid($"{ToJsonPointer(ex.Path)}: {ex.Message}");
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
    /// <remarks>
    /// Compared by value, as the schema's <c>const</c> compares it: <c>1.0</c> is the number 1, and
    /// rejecting it here as an unknown version would refuse a message the schema accepts.
    /// </remarks>
    private static string? CheckSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(SchemaVersionProperty, out var versionElement))
        {
            // Absent or not an object at all — the schema reports that better than this can.
            return null;
        }

        if (versionElement.ValueKind == JsonValueKind.Number &&
            versionElement.TryGetDecimal(out var version) &&
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
            .Where(detail => detail.Errors is { Count: > 0 } && !IsCondition(detail))
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
    /// The serializer names a field <c>$.measurements[0].measuredAt</c>; the schema errors name it
    /// <c>/measurements/0/measuredAt</c>. One style in the log. Only the models' own property names
    /// reach this — plain camelCase, which never needs the bracket-quoted form or pointer escaping.
    /// </summary>
    private static string ToJsonPointer(string? path) =>
        string.IsNullOrEmpty(path)
            ? ""
            : path.TrimStart('$').Replace('[', '.').Replace("]", "", StringComparison.Ordinal).Replace('.', '/');

    /// <summary>
    /// Whether the result belongs to an <c>if</c>: the schema's per-type rules (ADR-011) ask "is
    /// this an energy measurement?" of every measurement, and a "no" is how the rule decides not to
    /// apply, not a fault in the message. Only the <c>then</c> that does apply reports one.
    /// </summary>
    private static bool IsCondition(EvaluationResults detail)
    {
        var segments = detail.EvaluationPath.ToString().Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            // A property named "if" would appear after "properties"; this schema has none, but
            // the check stays exact.
            if (segments[i] == "if" && segments[i - 1] != "properties")
                return true;
        }

        return false;
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
