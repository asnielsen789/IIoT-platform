using IIoT.Core.Authentication;
using IIoT.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Forwarders;

/// <summary>
/// Delivers telemetry to a downstream gateway in the v1 readings format —
/// <c>docs/gateway-contract.md</c>. Levels only; a message with any other reading, or with a
/// reading that names its sensor, needs <see cref="GatewayV2Forwarder"/> (ADR-011).
/// </summary>
/// <remarks>The request, the token and the failure handling are <see cref="GatewayForwarder"/>'s.</remarks>
public sealed class GatewayV1Forwarder : GatewayForwarder
{
    /// <summary>The named <see cref="HttpClient"/> this typed client is registered under.</summary>
    public const string HttpClientName = "IIoT.GatewayV1";

    private readonly IOptions<GatewayV1Options> _options;

    public GatewayV1Forwarder(
        HttpClient httpClient,
        IAccessTokenProvider tokenProvider,
        IOptions<GatewayV1Options> options,
        ILogger<GatewayV1Forwarder> logger)
        : base(httpClient, tokenProvider, logger)
    {
        _options = options;
    }

    protected override int ContractVersion => GatewayV1Batch.ContractVersionV1;

    protected override Uri? BaseAddress => _options.Value.BaseAddress;

    protected override GatewayBody Map(TelemetryMessage message, ForwardingContext context)
    {
        var batch = GatewayV1Batch.From(message, context);
        return new GatewayBody(batch.ToUtf8Json(), batch.MessageId, batch.DeviceId, batch.Readings.Count);
    }
}
