namespace IIoT.Core.Forwarders;

/// <summary>Where the v1 gateway is.</summary>
public sealed class GatewayV1Options
{
    /// <summary>
    /// The gateway's base address; requests go to <c>{BaseAddress}/v1/readings</c>. Must be HTTPS,
    /// except for a loopback address. See <see cref="Security.TransportSecurity"/>.
    /// </summary>
    public Uri? BaseAddress { get; set; }
}
