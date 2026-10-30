namespace IIoT.Core.Forwarders;

/// <summary>Where the v2 gateway is (ADR-011). Setting it makes the platform forward with v2.</summary>
public sealed class GatewayV2Options
{
    /// <summary>
    /// The gateway's base address; requests go to <c>{BaseAddress}/v2/readings</c>. Must be HTTPS,
    /// except for a loopback address. See <see cref="Security.TransportSecurity"/>.
    /// </summary>
    public Uri? BaseAddress { get; set; }
}
