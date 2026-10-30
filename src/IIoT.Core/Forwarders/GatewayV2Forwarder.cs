using IIoT.Core.Authentication;
using IIoT.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Forwarders;

/// <summary>
/// Delivers telemetry to a downstream gateway in the v2 readings format — every reading kind of
/// ADR-011, each with its sensor — at <c>{BaseAddress}/v2/readings</c>. See
/// <c>docs/gateway-contract.md</c>, section 11.
/// </summary>
/// <remarks>
/// The request, the token and the failure handling are <see cref="GatewayForwarder"/>'s, exactly as
/// for v1: one request per message, the same <c>Idempotency-Key</c>, one retry on a <c>401</c>.
/// The platform sends one version, chosen by configuration: this one when <c>GatewayV2:BaseAddress</c>
/// is set.
/// </remarks>
public sealed class GatewayV2Forwarder : GatewayForwarder
{
    /// <summary>The named <see cref="HttpClient"/> this typed client is registered under.</summary>
    public const string HttpClientName = "IIoT.GatewayV2";

    private readonly IOptions<GatewayV2Options> _options;

    public GatewayV2Forwarder(
        HttpClient httpClient,
        IAccessTokenProvider tokenProvider,
        IOptions<GatewayV2Options> options,
        ILogger<GatewayV2Forwarder> logger)
        : base(httpClient, tokenProvider, logger)
    {
        _options = options;
    }

    protected override int ContractVersion => GatewayV2Batch.ContractVersionV2;

    protected override Uri? BaseAddress => _options.Value.BaseAddress;

    protected override GatewayBody Map(TelemetryMessage message, ForwardingContext context)
    {
        var batch = GatewayV2Batch.From(message, context);
        return new GatewayBody(batch.ToUtf8Json(), batch.MessageId, batch.DeviceId, batch.Readings.Count);
    }
}
