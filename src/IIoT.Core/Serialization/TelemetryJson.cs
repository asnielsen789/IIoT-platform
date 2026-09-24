using System.Text.Json;
using System.Text.Json.Serialization;

namespace IIoT.Core.Serialization;

/// <summary>
/// The single set of JSON options used for the telemetry contract, so the reader and any
/// future writer cannot drift apart.
/// </summary>
public static class TelemetryJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // The contract is camelCase; the models are PascalCase.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,

            // Unknown fields are ignored, not rejected — evolution rules, section 6.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
