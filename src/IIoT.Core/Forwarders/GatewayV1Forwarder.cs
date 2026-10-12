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
/// A typed HTTP client. Retries do not live here: the client is registered with a resilience
/// handler that retries transient failures (5xx, 408, 429, timeouts) with backoff and never
/// retries any other 4xx, so by the time this class sees a response, that response is final.
/// Retrying a POST is safe only because of the <c>Idempotency-Key</c> it carries.
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
        var accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        // Bytes rather than a streamed body, so the resilience handler can send the same request
        // again on a retry.
        request.Content = new ByteArrayContent(batch.ToUtf8Json());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add(IdempotencyKeyHeader, batch.MessageId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

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

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The gateway does not accept a token the cache still considers valid — revoked, or
            // signed with a key the provider has since rotated. Not retried, like every other
            // 4xx, but discarded, so the next message does not fail the same way until expiry.
            _tokenProvider.Invalidate(accessToken);
        }

        var detail = await ReadProblemDetailAsync(response, cancellationToken);
        throw new ForwardingException(
            $"Gateway v1 responded {(int)response.StatusCode} {response.StatusCode}{detail}")
        {
            StatusCode = response.StatusCode
        };
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
