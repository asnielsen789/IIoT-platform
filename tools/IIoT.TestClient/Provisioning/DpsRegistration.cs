using System.Text.Json;

namespace IIoT.TestClient.Provisioning;

/// <summary>
/// The registration request a device sends to DPS, and what each DPS answer means for the device.
/// </summary>
/// <remarks>
/// <para>
/// The request is a device registration object, <c>{"registrationId": "..."}</c>. Every answer
/// carries a <c>RegistrationOperationStatus</c>: <c>operationId</c> and <c>status</c>
/// (<c>assigning</c>, <c>assigned</c>, <c>failed</c>, <c>disabled</c>, <c>unassigned</c>), and once
/// the operation has finished, a <c>registrationState</c> with <c>assignedHub</c> and
/// <c>deviceId</c>, or <c>errorCode</c> and <c>errorMessage</c>. A refused request carries an
/// error object instead: <c>errorCode</c>, <c>message</c>, <c>trackingId</c>.
/// </para>
/// <para>
/// The status code on the response topic decides. 202 means still running: ask again after
/// <c>retry-after</c>. 200 means finished: assigned, or not. 429 means throttled: ask again after
/// <c>retry-after</c>. Anything else is a refusal, which a retry does not fix.
/// </para>
/// </remarks>
public static class DpsRegistration
{
    /// <summary>
    /// How long to wait when DPS gives no <c>retry-after</c>. DPS normally states one; three seconds
    /// is a conservative stand-in, since a registration typically completes in a few seconds.
    /// </summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(3);

    /// <summary>The shortest wait honoured, so a zero cannot turn polling into a busy loop.</summary>
    public static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait honoured, so a malformed or hostile value cannot stall the device.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The registration request body for <paramref name="registrationId"/>.</summary>
    public static byte[] RequestPayload(string registrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);

        return JsonSerializer.SerializeToUtf8Bytes(new { registrationId });
    }

    /// <summary>What a DPS answer means, from its topic and its body.</summary>
    public static DpsOutcome Read(DpsResponseTopic topic, ReadOnlySpan<byte> body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new DpsFailed(topic.StatusCode, null, $"DPS answered {topic.StatusCode} with a body that is not JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            return new DpsFailed(topic.StatusCode, null, $"DPS answered {topic.StatusCode} with a body that is not a JSON object.");

        switch (topic.StatusCode)
        {
            case 202:
                return String(root, "operationId") is { } operationId
                    ? new DpsInProgress(operationId, Wait(topic.RetryAfter))
                    : new DpsFailed(202, null, "DPS answered 202 without an operationId to poll.");

            case 200:
                return Finished(root);

            case 429:
                return new DpsThrottled(Wait(topic.RetryAfter));

            default:
                return new DpsFailed(
                    topic.StatusCode,
                    Number(root, "errorCode"),
                    String(root, "message") ?? $"DPS answered {topic.StatusCode}.");
        }
    }

    // A finished operation: assigned, or failed, disabled or unassigned, with the reason DPS gives.
    private static DpsOutcome Finished(JsonElement root)
    {
        var status = String(root, "status");
        var state = root.TryGetProperty("registrationState", out var registrationState) &&
                    registrationState.ValueKind == JsonValueKind.Object
            ? registrationState
            : (JsonElement?)null;

        if (string.Equals(status, "assigned", StringComparison.OrdinalIgnoreCase))
        {
            var hub = state is { } s ? String(s, "assignedHub") : null;
            var deviceId = state is { } t ? String(t, "deviceId") : null;

            return hub is not null && deviceId is not null
                ? new DpsAssigned(hub, deviceId, state is { } u ? String(u, "substatus") : null)
                : new DpsFailed(200, null, "DPS reported the device assigned, but named no hub or device ID.");
        }

        var errorCode = state is { } e ? Number(e, "errorCode") : null;
        var message = state is { } m ? String(m, "errorMessage") : null;

        return new DpsFailed(200, errorCode, message ?? $"DPS finished the registration with status '{status ?? "(none)"}'.");
    }

    private static TimeSpan Wait(TimeSpan? retryAfter) =>
        retryAfter is not { } value ? DefaultRetryAfter
        : value < MinimumRetryAfter ? MinimumRetryAfter
        : value > MaximumRetryAfter ? MaximumRetryAfter
        : value;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;
}

/// <summary>What a DPS answer means for the device.</summary>
public abstract record DpsOutcome;

/// <summary>The registration is still running: ask again with <paramref name="OperationId"/> after <paramref name="RetryAfter"/>.</summary>
public sealed record DpsInProgress(string OperationId, TimeSpan RetryAfter) : DpsOutcome;

/// <summary>DPS created the identity: connect to <paramref name="AssignedHub"/> as <paramref name="DeviceId"/>.</summary>
/// <param name="AssignedHub">The IoT hub's host name.</param>
/// <param name="DeviceId">The device ID in that hub: the registration ID, for an enrollment group.</param>
/// <param name="Substatus">How it came about, such as <c>initialAssignment</c> or <c>deviceDataMigrated</c>.</param>
public sealed record DpsAssigned(string AssignedHub, string DeviceId, string? Substatus) : DpsOutcome;

/// <summary>DPS is throttling the device: send the same request again after <paramref name="RetryAfter"/>.</summary>
public sealed record DpsThrottled(TimeSpan RetryAfter) : DpsOutcome;

/// <summary>DPS refused or failed the registration. Retrying does not help without a change.</summary>
/// <param name="StatusCode">The response topic's status code.</param>
/// <param name="ErrorCode">DPS's own error code, such as 401002, when it gave one.</param>
/// <param name="Reason">What DPS said, or why its answer could not be read.</param>
public sealed record DpsFailed(int StatusCode, long? ErrorCode, string Reason) : DpsOutcome;
