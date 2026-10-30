using System.Globalization;
using System.Net;

namespace IIoT.TestClient.Provisioning;

/// <summary>
/// The Device Provisioning Service's MQTT topics, and the parts of a response topic a device needs:
/// the status code, the request ID and the retry interval.
/// </summary>
/// <remarks>
/// <para>
/// DPS is not a general-purpose broker: it accepts exactly these topic names and the one topic
/// filter, nothing else. From "Use MQTT to communicate with Azure IoT DPS"
/// (https://learn.microsoft.com/azure/iot-dps/iot-mqtt-connect-to-iot-dps): subscribe to
/// <c>$dps/registrations/res/#</c>, publish the registration to
/// <c>$dps/registrations/PUT/iotdps-register/?$rid={request_id}</c>, and poll with
/// <c>$dps/registrations/GET/iotdps-get-operationstatus/?$rid={request_id}&amp;operationId={operationId}</c>.
/// Every answer arrives on <c>$dps/registrations/res/{status}/?$rid={request_id}</c>, with
/// <c>&amp;retry-after={seconds}</c> while the operation is still running.
/// </para>
/// <para>
/// The <c>$rid</c> is the device's own request ID, echoed back, so an answer can be matched to the
/// question it answers.
/// </para>
/// </remarks>
public static class DpsTopics
{
    /// <summary>The topic filter for every DPS response.</summary>
    public const string ResponseFilter = "$dps/registrations/res/#";

    private const string ResponsePrefix = "$dps/registrations/res/";

    /// <summary>The topic a device publishes its registration request to.</summary>
    public static string Register(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        return $"$dps/registrations/PUT/iotdps-register/?$rid={WebUtility.UrlEncode(requestId)}";
    }

    /// <summary>The topic a device publishes to, to ask how its registration operation is going.</summary>
    public static string OperationStatus(string requestId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return $"$dps/registrations/GET/iotdps-get-operationstatus/?$rid={WebUtility.UrlEncode(requestId)}" +
               $"&operationId={WebUtility.UrlEncode(operationId)}";
    }

    /// <summary>
    /// Reads a response topic. Returns <see langword="false"/> for anything that is not one: another
    /// topic, or a status segment that is not a number.
    /// </summary>
    public static bool TryParseResponse(string topic, out DpsResponseTopic response)
    {
        response = default;
        if (string.IsNullOrEmpty(topic) || !topic.StartsWith(ResponsePrefix, StringComparison.Ordinal))
            return false;

        var rest = topic[ResponsePrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var statusText = slash < 0 ? rest : rest[..slash];
        if (!int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            return false;

        string? requestId = null;
        TimeSpan? retryAfter = null;

        var query = slash < 0 ? string.Empty : rest[(slash + 1)..];
        if (query.StartsWith('?'))
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : WebUtility.UrlDecode(pair[(equals + 1)..]);

            if (name == "$rid")
                requestId = value;
            else if (name == "retry-after" &&
                     int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                retryAfter = TimeSpan.FromSeconds(seconds);
        }

        response = new DpsResponseTopic(status, requestId, retryAfter);
        return true;
    }
}

/// <summary>What a DPS response topic says.</summary>
/// <param name="StatusCode">The HTTP-style status: 202 while the operation runs, 200 when it has finished, 4xx or 5xx on an error.</param>
/// <param name="RequestId">The <c>$rid</c> the device sent, or <see langword="null"/> if the topic had none.</param>
/// <param name="RetryAfter">How long to wait before asking again, when DPS said so.</param>
public readonly record struct DpsResponseTopic(int StatusCode, string? RequestId, TimeSpan? RetryAfter);
