using System.Text;
using Azure.Core.Amqp;
using Azure.Messaging.EventHubs;
using IIoT.Core.Services;
using IIoT.Functions;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IIoT.Integration.Tests.Hosting;

/// <summary>
/// The Function's entry point, given an event exactly as the isolated worker receives one from
/// IoT Hub, and delivering through the real pipeline to the gateway mock.
/// </summary>
/// <remarks>
/// <para>
/// The host reads the event from the hub's built-in endpoint and passes the worker the raw AMQP
/// message; the worker's Event Hubs extension rebuilds it with
/// <c>new EventData(AmqpAnnotatedMessage.FromBytes(...))</c>. The test makes the same round trip,
/// carrying the annotations IoT Hub stamped on the first live message, so what the Function reads
/// from the transport runs as it does in Azure.
/// </para>
/// <para>
/// It guards what the first live message hit: Azure.Messaging.EventHubs 5.11.5 unboxed the
/// enqueued time as a <see cref="DateTimeOffset"/>, an AMQP timestamp decodes as a
/// <see cref="DateTime"/>, and so reading <see cref="EventData.EnqueuedTime"/> threw
/// <see cref="InvalidCastException"/> on every invocation.
/// </para>
/// </remarks>
public class TelemetryFunctionTests : IAsyncLifetime
{
    private const string AuthenticatedDevice = "tank-000123";

    // AMQP timestamps carry milliseconds, so the expected value has no finer precision.
    private static readonly DateTimeOffset EnqueuedAt = new(2026, 10, 15, 12, 42, 57, 954, TimeSpan.Zero);

    private readonly TestTokenIssuer _issuer = new();
    private readonly GatewayMockFactory _gateway;
    private readonly ForwardingPipeline _pipeline;

    public TelemetryFunctionTests()
    {
        _gateway = new GatewayMockFactory(_issuer.GatewaySettings());
        _pipeline = new ForwardingPipeline(_gateway, _issuer);
    }

    [Fact]
    public async Task EventFromIoTHub_IsForwarded_UnderTheHubsIdentity_WithItsEnqueuedTime()
    {
        var eventData = EventAsTheWorkerReceivesIt(ContractFixtures.ReadDevice("valid-full.json"));

        await using (var scope = _pipeline.Services.CreateAsyncScope())
        {
            var function = new TelemetryFunction(
                scope.ServiceProvider.GetRequiredService<TelemetryService>(),
                NullLogger<TelemetryFunction>.Instance);

            await function.ProcessTelemetry(eventData, new InvocationContext());
        }

        var stored = Assert.Single(_gateway.Store.Batches);
        Assert.Equal(AuthenticatedDevice, stored.DeviceId);
        Assert.Equal(EnqueuedAt, stored.Body.GetProperty("receivedAt").GetDateTimeOffset());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        await _gateway.DisposeAsync();
        _issuer.Dispose();
    }

    /// <summary>
    /// A device message as IoT Hub stamps it, serialized the way the host hands it to the worker
    /// and rebuilt the way the worker's Event Hubs extension rebuilds it.
    /// </summary>
    private static EventData EventAsTheWorkerReceivesIt(string payload)
    {
        var message = new AmqpAnnotatedMessage(AmqpMessageBody.FromData([Encoding.UTF8.GetBytes(payload)]));
        message.Properties.ContentType = "application/json";
        message.Properties.ContentEncoding = "utf-8";

        message.MessageAnnotations["iothub-connection-device-id"] = AuthenticatedDevice;
        message.MessageAnnotations["iothub-message-source"] = "Telemetry";
        message.MessageAnnotations["iothub-enqueuedtime"] = EnqueuedAt.UtcDateTime;
        message.MessageAnnotations["x-opt-sequence-number"] = 0L;
        message.MessageAnnotations["x-opt-offset"] = "0";
        message.MessageAnnotations["x-opt-enqueued-time"] = EnqueuedAt.UtcDateTime;

        return new EventData(AmqpAnnotatedMessage.FromBytes(message.ToBytes()));
    }

    /// <summary>The Function reads only the cancellation token from its context.</summary>
    private sealed class InvocationContext : FunctionContext
    {
        public override string InvocationId => "test-invocation";

        public override string FunctionId => nameof(TelemetryFunction.ProcessTelemetry);

        public override TraceContext TraceContext => throw new NotSupportedException();

        public override BindingContext BindingContext => throw new NotSupportedException();

        public override RetryContext RetryContext => throw new NotSupportedException();

        public override IServiceProvider InstanceServices { get; set; } = new ServiceCollection().BuildServiceProvider();

        public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();

        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

        public override IInvocationFeatures Features => throw new NotSupportedException();
    }
}
