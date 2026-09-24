using System.Reflection;
using Json.Schema;

namespace IIoT.Core.Validation;

/// <summary>
/// Loads the normative telemetry schema.
/// </summary>
/// <remarks>
/// The schema file is not copied into this project. <c>docs/schemas/telemetry-v1.schema.json</c>
/// is linked into the assembly as an embedded resource, so the document the code validates
/// against and the document the contract publishes are the same bytes — ADR-004 requires that
/// there be exactly one copy.
/// </remarks>
public static class TelemetrySchema
{
    private const string ResourceName = "IIoT.Core.Schemas.telemetry-v1.schema.json";

    /// <summary>The version this build understands. Any other value is rejected outright.</summary>
    public const int SupportedVersion = 1;

    private static readonly Lazy<JsonSchema> LazySchema = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The parsed schema. Loaded once, on first use.</summary>
    public static JsonSchema Instance => LazySchema.Value;

    private static JsonSchema Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{ResourceName}' was not found. It is linked in from docs/schemas/ " +
                "by IIoT.Core.csproj; check that the EmbeddedResource item and its LogicalName still match.");

        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
