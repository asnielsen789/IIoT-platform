using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace IIoT.GatewayMock.Hosting;

internal static class ForwardedHeadersSetup
{
    /// <summary>
    /// Registers the forwarded-headers rules if they apply, and says whether they do. The caller
    /// adds the middleware, first in the pipeline, only then.
    /// </summary>
    public static bool AddGatewayForwardedHeaders(
        this IServiceCollection services, IConfiguration configuration, bool dashboardEnabled)
    {
        var settings = configuration.GetSection(GatewayForwardedHeadersOptions.SectionName)
            .Get<GatewayForwardedHeadersOptions>() ?? new GatewayForwardedHeadersOptions();

        if (!settings.IsEnabled(dashboardEnabled))
        {
            return false;
        }

        services.AddOptions<GatewayForwardedHeadersOptions>()
            .BindConfiguration(GatewayForwardedHeadersOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GatewayForwardedHeadersOptions>, GatewayForwardedHeadersOptionsValidator>();
        services.ConfigureOptions<ConfigureForwardedHeadersOptions>();

        return true;
    }
}

/// <summary>
/// Turns <see cref="GatewayForwardedHeadersOptions"/> into the forwarded-headers middleware's rules.
/// </summary>
internal sealed class ConfigureForwardedHeadersOptions : IConfigureOptions<ForwardedHeadersOptions>
{
    private readonly IOptions<GatewayForwardedHeadersOptions> _settings;

    public ConfigureForwardedHeadersOptions(IOptions<GatewayForwardedHeadersOptions> settings)
    {
        _settings = settings;
    }

    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;

        // ForwardLimit stays at its default of 1: only the rightmost value, the one the proxy next
        // to the app wrote, is read. Anything a client put further left is ignored.
        //
        // The loopback entries the framework puts in KnownProxies and KnownIPNetworks stay; the
        // configured networks are added to them.
        foreach (var network in _settings.Value.KnownNetworks)
        {
            options.KnownIPNetworks.Add(IPNetwork.Parse(network));
        }
    }
}

/// <summary>
/// Refuses to start with a trusted network that cannot be read, rather than starting with fewer
/// trusted networks than the operator meant, or none.
/// </summary>
internal sealed class GatewayForwardedHeadersOptionsValidator : IValidateOptions<GatewayForwardedHeadersOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayForwardedHeadersOptions options)
    {
        var failures = new List<string>();

        for (var i = 0; i < options.KnownNetworks.Count; i++)
        {
            if (!IPNetwork.TryParse(options.KnownNetworks[i], out _))
            {
                failures.Add(
                    $"{GatewayForwardedHeadersOptions.SectionName}:KnownNetworks:{i} '{options.KnownNetworks[i]}' is not a network in CIDR notation, such as 10.0.0.0/8.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
