using IIoT.Core.Authentication;
using IIoT.Core.Forwarders;
using IIoT.Core.Interfaces;
using IIoT.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IIoT.Functions;

/// <summary>
/// Registers the processing pipeline: <see cref="TelemetryService"/>, the gateway forwarder and
/// the token provider it authenticates with.
/// </summary>
/// <remarks>
/// <para>Kept out of <c>Program.cs</c> so the integration tests compose exactly these registrations.</para>
/// <para>
/// One gateway contract version is spoken (ADR-011): <see cref="GatewayV2Forwarder"/> when
/// <c>GatewayV2:BaseAddress</c> is set, otherwise <see cref="GatewayV1Forwarder"/>. Each version
/// has its own section of the same shape, <c>BaseAddress</c> and an optional <c>Resilience</c>, so
/// moving to v2 is setting one address, and moving back is removing it.
/// </para>
/// <para>
/// The lifetimes are the point, and they follow from <see cref="HttpClient"/>. A typed client is
/// transient: each resolution gets a fresh <see cref="HttpClient"/> over a handler that
/// <see cref="IHttpClientFactory"/> pools and recycles, so connections are reused and DNS changes
/// are still picked up. Anything that holds a typed client for longer than one unit of work
/// defeats that — it keeps one handler, and one DNS answer, for the life of the process.
/// </para>
/// <list type="bullet">
/// <item><see cref="IDataForwarder"/> → <see cref="GatewayV1Forwarder"/> or
/// <see cref="GatewayV2Forwarder"/>: a typed client, so transient.</item>
/// <item><see cref="TelemetryService"/>: scoped — one per function invocation, which the isolated
/// worker runs in its own scope. It was a singleton, which captured the first forwarder and its
/// <see cref="HttpClient"/> for good; the forwarder itself was also registered a second time as a
/// singleton, bypassing the factory entirely.</item>
/// <item><see cref="IAccessTokenProvider"/>: a singleton, because its cache must outlive an
/// invocation. It holds <see cref="IHttpClientFactory"/>, never an <see cref="HttpClient"/>.</item>
/// </list>
/// <para>
/// Nothing here is validated at startup. A gateway or token endpoint that is not configured yet
/// makes each message fail forwarding with the reason logged, but leaves the Function validating
/// and logging device traffic — which is what end-to-end testing before a gateway exists needs.
/// </para>
/// </remarks>
public static class ForwardingServiceCollectionExtensions
{
    /// <summary><c>GatewayV1__BaseAddress</c> as an app setting.</summary>
    public const string GatewaySection = "GatewayV1";

    /// <summary>
    /// Retry and timeout settings for gateway calls, bound onto the standard resilience options
    /// (for example <c>GatewayV1__Resilience__Retry__MaxRetryAttempts</c>). Optional.
    /// </summary>
    public const string GatewayResilienceSection = "GatewayV1:Resilience";

    /// <summary>
    /// <c>GatewayV2__BaseAddress</c> as an app setting. Setting it makes the platform forward with
    /// gateway contract v2 (ADR-011).
    /// </summary>
    public const string GatewayV2Section = "GatewayV2";

    /// <summary>As <see cref="GatewayResilienceSection"/>, for v2. Optional.</summary>
    public const string GatewayV2ResilienceSection = "GatewayV2:Resilience";

    /// <summary>
    /// <c>GatewayAuth__TokenEndpoint</c>, <c>__ClientId</c>, <c>__ClientSecret</c>,
    /// <c>__Audience</c>. In Azure the secret is a Key Vault reference.
    /// </summary>
    public const string GatewayAuthSection = "GatewayAuth";

    public static IServiceCollection AddTelemetryForwarding(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<GatewayV1Options>().Bind(configuration.GetSection(GatewaySection));
        services.AddOptions<GatewayV2Options>().Bind(configuration.GetSection(GatewayV2Section));
        services.AddOptions<ClientCredentialsOptions>().Bind(configuration.GetSection(GatewayAuthSection));

        // The token endpoint gets the standard pipeline with its defaults: a token request is
        // safe to repeat, and one transient failure there should not cost a message.
        services.AddHttpClient(ClientCredentialsTokenProvider.HttpClientName)
            .AddStandardResilienceHandler();
        services.AddSingleton<IAccessTokenProvider, ClientCredentialsTokenProvider>();

        // The standard pipeline retries exactly the transient failures — 5xx, 408, 429, network
        // errors and timeouts — with exponential backoff and jitter, honours Retry-After, and
        // never retries any other 4xx. It retries POST too, which is safe here only because every
        // request carries an Idempotency-Key the gateway deduplicates on.
        var useV2 = UsesGatewayV2(configuration);
        var gatewayClient = useV2
            ? services.AddHttpClient<IDataForwarder, GatewayV2Forwarder>(GatewayV2Forwarder.HttpClientName)
            : services.AddHttpClient<IDataForwarder, GatewayV1Forwarder>(GatewayV1Forwarder.HttpClientName);
        var resilience = configuration.GetSection(useV2 ? GatewayV2ResilienceSection : GatewayResilienceSection);

        // The section is optional, but the overload that binds it throws when the section is
        // absent — at startup, before any function is indexed.
        if (resilience.Exists())
            gatewayClient.AddStandardResilienceHandler(resilience);
        else
            gatewayClient.AddStandardResilienceHandler();

        services.AddScoped<TelemetryService>();

        return services;
    }

    /// <summary>
    /// Whether the platform forwards with gateway contract v2: <c>GatewayV2:BaseAddress</c> is set.
    /// Its value is checked per message, like v1's, so a wrong one fails forwarding with the reason.
    /// </summary>
    public static bool UsesGatewayV2(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetSection(GatewayV2Section)["BaseAddress"]);
}
