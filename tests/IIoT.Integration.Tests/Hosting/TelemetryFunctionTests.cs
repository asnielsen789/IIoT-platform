using System.Net;
using Azure;
using Azure.Messaging.EventHubs;
using IIoT.Core.DeadLettering;
using IIoT.Core.Forwarders;
using IIoT.Core.Services;
using IIoT.Functions;
using IIoT.Functions.DeadLettering;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IIoT.Integration.Tests.Hosting;

/// <summary>
/// The Function's entry point, given an event exactly as the isolated worker receives one from
/// IoT Hub (<see cref="IoTHubEvents"/>).
/// </summary>
/// <remarks>
/// <para>
/// The first test delivers through the real pipeline to the gateway mock. It guards what the first
/// live message hit: Azure.Messaging.EventHubs 5.11.5 unboxed the enqueued time as a
/// <see cref="DateTimeOffset"/>, an AMQP timestamp decodes as a <see cref="DateTime"/>, and so
/// reading <see cref="EventData.EnqueuedTime"/> threw <see cref="InvalidCastException"/> on every
/// invocation.
/// </para>
/// <para>
/// The others are about dead-lettering (ADR-010), with a forwarder that fails on demand: what is
/// kept, what is not, and what happens when keeping it fails.
/// </para>
/// </remarks>
public class TelemetryFunctionTests : IAsyncLifetime
{
    private const string AuthenticatedDevice = "tank-000123";
    private const string MessageId = "01J8F2K9QS4T7V0X3Z6B8N1M5B";

    // AMQP timestamps carry milliseconds, so the expected value has no finer precision.
    private static readonly DateTimeOffset EnqueuedAt = new(2026, 10, 15, 12, 42, 57, 954, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 43, 29, 0, TimeSpan.Zero);

    private readonly TestTokenIssuer _issuer = new();
    private readonly GatewayMockFactory _gateway;
    private readonly ForwardingPipeline _pipeline;

    private readonly ScriptedForwarder _forwarder = new();
    private readonly RecordingDeadLetterQueue _queue = new();
    private readonly CapturingLoggerProvider _logs = new();

    public TelemetryFunctionTests()
    {
        _gateway = new GatewayMockFactory(_issuer.GatewaySettings());
        _pipeline = new ForwardingPipeline(_gateway, _issuer);
    }

    [Fact]
    public async Task EventFromIoTHub_IsForwarded_UnderTheHubsIdentity_WithItsEnqueuedTime()
    {
        var eventData = IoTHubEvents.AsTheWorkerReceivesIt(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        await _pipeline.ProcessTelemetryAsync(eventData);

        var stored = Assert.Single(_gateway.Store.Batches);
        Assert.Equal(AuthenticatedDevice, stored.DeviceId);
        Assert.Equal(EnqueuedAt, stored.Body.GetProperty("receivedAt").GetDateTimeOffset());
        Assert.Empty(_pipeline.DeadLetters.Added);
    }

    /// <summary>
    /// A message whose forwarding failed is kept: exactly one entry, carrying the payload exactly as
    /// IoT Hub delivered it, the authenticated identity, the enqueued time, the message id, the
    /// failure with its status code, and when it was written. It is written invisible for one retry
    /// interval, so the first retry does not follow straight on the failure.
    /// </summary>
    [Fact]
    public async Task ForwardingFailed_WritesExactlyOneEnvelope_WithEveryField()
    {
        var payload = ContractFixtures.ReadDevice("valid-full.json");
        _forwarder.FailWith = new ForwardingException("Gateway v1 responded 503 ServiceUnavailable")
        {
            StatusCode = HttpStatusCode.ServiceUnavailable
        };

        await Function().ProcessTelemetry(IoTHubEvents.AsTheWorkerReceivesIt(payload, AuthenticatedDevice, EnqueuedAt), new TestFunctionContext());

        var (entry, initialDelay) = Assert.Single(_queue.Added);
        Assert.Equal(ForwardingRetrySchedule.DefaultRetryInterval, initialDelay);
        Assert.Empty(_queue.Poisoned);

        var envelope = DeadLetterEnvelopeJson.Read(entry).Envelope!;
        Assert.Equal(
            DeadLetterEnvelope.Create(payload, AuthenticatedDevice, EnqueuedAt, MessageId,
                "Gateway v1 responded 503 ServiceUnavailable", 503, Now),
            envelope);

        var logged = Assert.Single(_logs.For<ForwardingDeadLetterWriter>());
        Assert.Equal(LogLevel.Information, logged.Level);
        Assert.Equal(MessageId, logged.Properties["MessageId"]);
    }

    /// <summary>
    /// A rejection is final — the same payload fails the same way every time — and is already logged,
    /// so nothing is kept. An identity mismatch in particular is a security event, not a message to
    /// try again.
    /// </summary>
    [Theory]
    [InlineData("valid-full.json", "tank-000999")]
    [InlineData("invalid-negative-level.json", AuthenticatedDevice)]
    [InlineData("valid-full.json", null)]
    public async Task RejectedMessage_IsNotDeadLettered(string fixture, string? deviceId)
    {
        _forwarder.FailWith = new ForwardingException("never reached");

        await Function().ProcessTelemetry(
            IoTHubEvents.AsTheWorkerReceivesIt(ContractFixtures.ReadDevice(fixture), deviceId ?? "", EnqueuedAt),
            new TestFunctionContext());

        Assert.Empty(_forwarder.Calls);
        Assert.Empty(_queue.Added);
        Assert.Empty(_queue.Poisoned);
    }

    [Fact]
    public async Task ForwardedMessage_IsNotDeadLettered()
    {
        await Function().ProcessTelemetry(
            IoTHubEvents.AsTheWorkerReceivesIt(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt),
            new TestFunctionContext());

        Assert.Single(_forwarder.Calls);
        Assert.Empty(_queue.Added);
    }

    /// <summary>
    /// If the entry cannot be written, the message is lost, and the Critical log line is the only
    /// record of it, so it names the message, the device and why forwarding failed. The function
    /// returns normally: the Event Hubs trigger would advance past a failed invocation anyway.
    /// </summary>
    [Fact]
    public async Task DeadLetterWriteFails_IsLoggedCritical_WithTheMessageDeviceAndReason()
    {
        _forwarder.FailWith = new HttpRequestException("Connection refused (gateway.iiot.test:443)");
        _queue.AddFailsWith = new RequestFailedException(403, "This request is not authorized to perform this operation.");

        await Function().ProcessTelemetry(
            IoTHubEvents.AsTheWorkerReceivesIt(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt),
            new TestFunctionContext());

        var critical = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Equal(MessageId, critical.Properties["MessageId"]);
        Assert.Equal(AuthenticatedDevice, critical.Properties["DeviceId"]);
        Assert.Equal("Connection refused (gateway.iiot.test:443)", critical.Properties["Reason"]);
        Assert.Contains("This request is not authorized", (string)critical.Properties["Problem"]!, StringComparison.Ordinal);
        Assert.Same(_queue.AddFailsWith, critical.Exception);
    }

    /// <summary>
    /// The connection settings are absent — the worker started without them — so the write fails with
    /// the setting named, and that is logged Critical rather than crashing the invocation.
    /// </summary>
    [Fact]
    public async Task DeadLetterStorageNotConfigured_IsLoggedCritical_NamingTheSetting()
    {
        _forwarder.FailWith = new ForwardingException("Gateway v1 BaseAddress is not configured");
        var function = Function(new StorageDeadLetterQueue(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));

        await function.ProcessTelemetry(
            IoTHubEvents.AsTheWorkerReceivesIt(ContractFixtures.ReadDevice("valid-full.json"), AuthenticatedDevice, EnqueuedAt),
            new TestFunctionContext());

        var critical = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("DeadLetterStorage__queueServiceUri", (string)critical.Properties["Problem"]!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry too large for one queue message is refused whole, never truncated: a truncated payload
    /// would fail validation on every retry. Logged Critical with the size and the limit.
    /// </summary>
    [Fact]
    public async Task EntryTooLargeForAQueueMessage_IsNotWritten_AndIsLoggedCritical()
    {
        var payload = ContractFixtures.ReadDevice("valid-minimal.json")
            .Replace("\"schemaVersion\": 1,", $"\"schemaVersion\": 1, \"padding\": \"{new string('p', 60_000)}\",", StringComparison.Ordinal);
        _forwarder.FailWith = new ForwardingException("Gateway v1 responded 502 BadGateway") { StatusCode = HttpStatusCode.BadGateway };

        await Function().ProcessTelemetry(IoTHubEvents.AsTheWorkerReceivesIt(payload, AuthenticatedDevice, EnqueuedAt), new TestFunctionContext());

        Assert.Single(_forwarder.Calls);
        Assert.Empty(_queue.Added);

        var critical = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains($"above the {DeadLetterEnvelopeJson.MaxEntryBytes}-byte limit", (string)critical.Properties["Problem"]!, StringComparison.Ordinal);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        await _gateway.DisposeAsync();
        _issuer.Dispose();
    }

    private TelemetryFunction Function(IDeadLetterQueue? queue = null)
    {
        var loggers = new LoggerFactory([_logs]);
        var service = new TelemetryService(_forwarder, loggers.CreateLogger<TelemetryService>());
        var writer = new ForwardingDeadLetterWriter(
            queue ?? _queue,
            ForwardingRetrySchedule.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            new FixedTimeProvider(Now),
            loggers.CreateLogger<ForwardingDeadLetterWriter>());

        return new TelemetryFunction(service, writer, NullLogger<TelemetryFunction>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
