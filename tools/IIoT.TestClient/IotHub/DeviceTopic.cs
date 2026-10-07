using System.Net;

namespace IIoT.TestClient.IotHub;

/// <summary>
/// Builds the device-to-cloud telemetry topic, including the property bag that carries content
/// type and content encoding.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most load-bearing detail in the test client. Section 1 of
/// <c>docs/mqtt-payload.md</c> requires both <c>$.ct</c> and <c>$.ce</c>: IoT Hub can only route
/// on message <em>content</em> when they are present, and without them content-based routing
/// fails silently — messages are accepted, nothing is routed, and no error is reported anywhere.
/// </para>
/// <para>
/// Property-bag keys are literal. Only the values are URL-encoded, which is why
/// <c>application/json</c> appears as <c>application%2Fjson</c> while <c>$.ct</c> keeps its
/// <c>$</c> and <c>.</c> unescaped.
/// </para>
/// </remarks>
public static class DeviceTopic
{
    /// <summary>IoT Hub's property-bag key for contentType.</summary>
    public const string ContentTypeKey = "$.ct";

    /// <summary>IoT Hub's property-bag key for contentEncoding.</summary>
    public const string ContentEncodingKey = "$.ce";

    /// <summary>The contract's content type.</summary>
    public const string ContentType = "application/json";

    /// <summary>The contract's content encoding.</summary>
    public const string ContentEncoding = "utf-8";

    /// <summary>Builds the full publish topic for <paramref name="deviceId"/>.</summary>
    public static string Events(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return $"devices/{deviceId}/messages/events/{PropertyBag}";
    }

    /// <summary>
    /// The encoded property bag, without the leading topic segments. Exposed so a test can pin
    /// the exact encoding independently of the device id.
    /// </summary>
    public static string PropertyBag { get; } =
        $"{ContentTypeKey}={WebUtility.UrlEncode(ContentType)}&" +
        $"{ContentEncodingKey}={WebUtility.UrlEncode(ContentEncoding)}";
}
