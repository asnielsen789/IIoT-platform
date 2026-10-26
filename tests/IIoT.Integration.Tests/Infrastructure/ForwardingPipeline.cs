using System.Collections.Concurrent;
using System.Net;
using Azure.Messaging.EventHubs;
using IIoT.Core.Authentication;
using IIoT.Core.Forwarders;
using IIoT.Core.Services;
using IIoT.Functions;
using IIoT.Functions.DeadLettering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// The platform's processing pipeline, composed by the same registration the Function uses, and
/// delivering to an in-process gateway mock.
/// </summary>
/// <remarks>
/// Only the network edge is replaced. <see cref="ForwardingServiceCollectionExtensions.AddTelemetryForwarding"/>
/// registers the real <see cref="TelemetryService"/>, forwarder, token provider and resilience
/// pipeline exactly as production does; the tests then swap the two primary handlers at the bottom
/// of those pipelines — the gateway client's for the mock's test server, the token client's for
/// the local token endpoint — so no socket is ever opened. Dead-letter handling is registered the
/// same way, by <see cref="DeadLetterServiceCollectionExtensions.AddForwardingDeadLetters"/>, with
/// the Storage queues swapped for <see cref="DeadLetters"/>.
/// </remarks>
public sealed class ForwardingPipeline : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public ForwardingPipeline(
        GatewayMockFactory gateway,
        TestTokenIssuer issuer,
        Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // The test server answers any host; localhost keeps the address loopback, the one
            // case in which the forwarder accepts plain HTTP.
            ["GatewayV1:BaseAddress"] = "http://localhost/",

            ["GatewayAuth:TokenEndpoint"] = TestTokenIssuer.TokenEndpoint,
            ["GatewayAuth:ClientId"] = TestTokenIssuer.ClientId,
            ["GatewayAuth:ClientSecret"] = issuer.ClientSecret,
            ["GatewayAuth:Audience"] = TestTokenIssuer.Audience,

            // The production retry policy with its timing shortened: the same attempts and the
            // same decisions about what to retry, in milliseconds instead of seconds.
            ["GatewayV1:Resilience:Retry:Delay"] = "00:00:00.010"
        };
        configure?.Invoke(settings);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(Logs));
        services.AddTelemetryForwarding(configuration);
        services.AddForwardingDeadLetters(configuration);
        services.Replace(ServiceDescriptor.Singleton<IDeadLetterQueue>(DeadLetters));

        services.AddHttpClient(GatewayV1Forwarder.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new GatewayTrafficHandler(Traffic) { InnerHandler = gateway.Server.CreateHandler() });
        services.AddHttpClient(ClientCredentialsTokenProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(issuer.CreateTokenEndpointHandler);

        // Scope validation catches a singleton that captures a scoped service; ValidateOnBuild
        // catches a registration that cannot be constructed at all.
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Every attempt the forwarder's HTTP client made, after its resilience handler.</summary>
    public GatewayTraffic Traffic { get; } = new();

    /// <summary>What was written to the dead-letter queues.</summary>
    public RecordingDeadLetterQueue DeadLetters { get; } = new();

    /// <summary>Everything the composed services logged.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => _services;

    /// <summary>Processes one message the way one function invocation does: in its own scope.</summary>
    public async Task<ProcessingOutcome> ProcessAsync(string payload, string authenticatedDeviceId, DateTimeOffset enqueuedAt)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TelemetryService>();
        return (await service.ProcessAsync(payload, authenticatedDeviceId, enqueuedAt)).Outcome;
    }

    /// <summary>
    /// Runs <see cref="TelemetryFunction.ProcessTelemetry"/> on <paramref name="eventData"/>, the
    /// function created in its own scope as the worker creates it.
    /// </summary>
    public async Task ProcessTelemetryAsync(EventData eventData)
    {
        await using var scope = _services.CreateAsyncScope();
        var function = ActivatorUtilities.CreateInstance<TelemetryFunction>(scope.ServiceProvider);
        await function.ProcessTelemetry(eventData, new TestFunctionContext());
    }

    /// <summary>
    /// Runs <see cref="RetryForwardingFunction.RetryForwarding"/> on <paramref name="entry"/> as its
    /// <paramref name="attempt"/>th dequeue, the function created in its own scope.
    /// </summary>
    public async Task RetryForwardingAsync(string entry, long attempt)
    {
        await using var scope = _services.CreateAsyncScope();
        var function = ActivatorUtilities.CreateInstance<RetryForwardingFunction>(scope.ServiceProvider);
        await function.RetryForwarding(entry, TestFunctionContext.ForQueueAttempt(attempt));
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>
/// What reached the gateway's network edge, attempt by attempt, with the option to fail some
/// attempts there before they reach the mock.
/// </summary>
public sealed class GatewayTraffic
{
    private int _attempts;

    /// <summary>
    /// Decides, per 1-based attempt number, whether to answer with a fault instead of passing the
    /// request on. <c>null</c> passes it on.
    /// </summary>
    public Func<int, HttpStatusCode?> Fault { get; set; } = _ => null;

    /// <summary>
    /// Decides, per 1-based attempt number, whether the request reaches the gateway but its answer
    /// is replaced with this status on the way back — a gateway that stored the batch and a response
    /// the sender never saw. <c>null</c> returns the gateway's own answer.
    /// </summary>
    public Func<int, HttpStatusCode?> LoseResponse { get; set; } = _ => null;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The status each attempt was answered with, in order.</summary>
    public ConcurrentQueue<HttpStatusCode> Responses { get; } = new();

    /// <summary>The <c>Idempotency-Key</c> each attempt carried, in order.</summary>
    public ConcurrentQueue<string?> IdempotencyKeys { get; } = new();

    internal int NextAttempt() => Interlocked.Increment(ref _attempts);
}

internal sealed class GatewayTrafficHandler : DelegatingHandler
{
    private readonly GatewayTraffic _traffic;

    public GatewayTrafficHandler(GatewayTraffic traffic)
    {
        _traffic = traffic;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = _traffic.NextAttempt();
        _traffic.IdempotencyKeys.Enqueue(
            request.Headers.TryGetValues(GatewayRequests.IdempotencyKeyHeader, out var keys) ? keys.Single() : null);

        var response = _traffic.Fault(attempt) is { } fault
            ? new HttpResponseMessage(fault) { RequestMessage = request }
            : await base.SendAsync(request, cancellationToken);

        if (_traffic.LoseResponse(attempt) is { } lost)
        {
            response.Dispose();
            response = new HttpResponseMessage(lost) { RequestMessage = request };
        }

        _traffic.Responses.Enqueue(response.StatusCode);
        return response;
    }
}
