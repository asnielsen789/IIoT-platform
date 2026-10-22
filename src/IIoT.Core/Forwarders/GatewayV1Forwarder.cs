using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IIoT.Core.Authentication;
using IIoT.Core.Interfaces;
using IIoT.Core.Models;
using IIoT.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Forwarders;

/// <summary>
/// Delivers telemetry to a downstream gateway in the v1 readings format —
/// <c>docs/gateway-contract.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The adapter pattern in one class: a new downstream target is a new <see cref="IDataForwarder"/>,
/// not a change to the processing pipeline.
/// </para>
/// <para>
/// A typed HTTP client. Transient retries do not live here: the client is registered with a
/// resilience handler that retries transient failures (5xx, 408, 429, timeouts) with backoff and
/// never retries any other 4xx, so by the time this class sees a response, the handler is done
/// with it. Retrying a POST is safe only because of the <c>Idempotency-Key</c> it carries.
/// </para>
/// <para>
/// The one retry that does live here is for <c>401 Unauthorized</c>, because only this class can
/// make it: the request has to go again with a different token, and the resilience handler can
/// only resend the request it was given, stale token included. One retry, with a newly obtained
/// token and the same body and <c>Idempotency-Key</c>, so a gateway that did store the first
/// attempt still stores the batch once. The retry is a second <see cref="HttpClient.SendAsync(HttpRequestMessage, CancellationToken)"/>,
/// so it runs through the resilience handler again — its own transient retries, its own total
/// timeout — but it happens at most once per message, so the worst case is two passes through
/// that pipeline, never a loop. A <c>403 Forbidden</c> is not retried: the token was accepted and
/// the caller is not allowed, which a new token for the same client does not change.
/// </para>
/// </remarks>
public sealed class GatewayV1Forwarder : IDataForwarder
{
    /// <summary>The named <see cref="HttpClient"/> this typed client is registered under.</summary>
    public const string HttpClientName = "IIoT.GatewayV1";

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private const string ReadingsPath = "v1/readings";
    private const string BaseAddressSetting = "Gateway v1 BaseAddress";
    private const int MaxReportedLength = 300;

    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly IOptions<GatewayV1Options> _options;
    private readonly ILogger<GatewayV1Forwarder> _logger;

    public GatewayV1Forwarder(
        HttpClient httpClient,
        IAccessTokenProvider tokenProvider,
        IOptions<GatewayV1Options> options,
        ILogger<GatewayV1Forwarder> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
    }

    /// <exception cref="ForwardingException">The gateway refused the batch, or it could not be sent.</exception>
    /// <exception cref="AccessTokenException">No token could be obtained to send it with.</exception>
    public async Task ForwardAsync(
        TelemetryMessage message,
        ForwardingContext context,
        CancellationToken cancellationToken = default)
    {
        // Settled before a token is fetched: a gateway address that will be refused should not
        // cost a call to the identity provider.
        var endpoint = ResolveEndpoint(_options.Value);
        var batch = GatewayV1Batch.From(message, context);

        // Serialised once: a retry with a new token must send exactly the same bytes.
        var body = batch.ToUtf8Json();

        var accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
        var response = await PostAsync(endpoint, body, batch.MessageId, accessToken, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The gateway does not accept a token the cache still considers valid — revoked, or
            // signed with a key the provider has since rotated. Every instance meets this once per
            // rotation, with the token it has cached, so it is discarded and the same request sent
            // once more with a new one rather than costing the message.
            response.Dispose();
            _tokenProvider.Invalidate(accessToken);

            _logger.LogWarning(
                "Gateway v1 rejected the access token for message {MessageId} from {DeviceId} (401); " +
                "retrying once with a new token",
                batch.MessageId,
                batch.DeviceId);

            accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
            response = await PostAsync(endpoint, body, batch.MessageId, accessToken, cancellationToken);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Delivered message {MessageId} from {DeviceId} with {ReadingCount} reading(s) to gateway v1 ({StatusCode})",
                    batch.MessageId,
                    batch.DeviceId,
                    batch.Readings.Count,
                    (int)response.StatusCode);
                return;
            }

            var detail = await ReadProblemDetailAsync(response, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A token issued moments ago was refused as well, so the token is not the problem:
                // the gateway does not trust what the identity provider issues for this client —
                // issuer, audience or signing keys disagree. Another token would fare no better, so
                // this one is not discarded either: the next message tries it first, which costs a
                // request to the gateway rather than one to the provider's rate-limited token
                // endpoint, and succeeds as soon as the gateway catches up with a key rotation.
                _logger.LogError(
                    "Authentication failure: gateway v1 rejected a newly obtained access token for message " +
                    "{MessageId} from {DeviceId} as well (401){Detail}. Not retried again; check that the " +
                    "gateway trusts the identity provider's issuer, audience and signing keys",
                    batch.MessageId,
                    batch.DeviceId,
                    detail);

                throw new ForwardingException(
                    $"Gateway v1 authentication failed: it responded 401 Unauthorized to a newly obtained token as well{detail}")
                {
                    StatusCode = response.StatusCode
                };
            }

            throw new ForwardingException(
                $"Gateway v1 responded {(int)response.StatusCode} {response.StatusCode}{detail}")
            {
                StatusCode = response.StatusCode
            };
        }
    }

    /// <summary>
    /// One <c>POST /v1/readings</c>, through the resilience handler. A new request each time:
    /// <see cref="HttpClient"/> refuses to send the same <see cref="HttpRequestMessage"/> twice.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(
        Uri endpoint,
        byte[] body,
        string idempotencyKey,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        // Bytes rather than a streamed body, so the resilience handler can send the same request
        // again on a retry.
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// <c>{BaseAddress}/v1/readings</c>, or a refusal: HTTPS only, except for a loopback address
    /// — <see cref="TransportSecurity"/>. Checked on every call rather than at startup, so a
    /// gateway that is not configured yet fails forwarding, with this reason in the log, without
    /// stopping the Function from validating and logging device traffic.
    /// </summary>
    private static Uri ResolveEndpoint(GatewayV1Options options)
    {
        var problem = TransportSecurity.DescribeViolation(options.BaseAddress, BaseAddressSetting);
        if (problem is not null)
        {
            throw new ForwardingException(problem);
        }

        // Without a trailing slash, a relative path replaces the last segment of the base
        // instead of being appended to it: https://host/api + v1/readings = https://host/v1/readings.
        var baseAddress = new UriBuilder(options.BaseAddress!);
        if (!baseAddress.Path.EndsWith('/'))
        {
            baseAddress.Path += "/";
        }

        return new Uri(baseAddress.Uri, ReadingsPath);
    }

    /// <summary>The <c>detail</c> of an RFC 9457 problem response, when the gateway sent one.</summary>
    private static async Task<string> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return "";
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("detail", out var detail) &&
                detail.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                var text = detail.GetString()!;
                return ": " + (text.Length <= MaxReportedLength ? text : text[..MaxReportedLength] + "...");
            }
        }
        catch (JsonException)
        {
            // Not a problem document. The status code alone has to do.
        }

        return "";
    }
}
