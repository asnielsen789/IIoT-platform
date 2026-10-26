using System.Net;
using IIoT.Core.Authentication;
using IIoT.Core.Forwarders;
using IIoT.Core.Models;
using IIoT.Core.Services;
using IIoT.Core.Tests.Fixtures;
using IIoT.Core.Tests.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IIoT.Core.Tests.Services;

public class TelemetryServiceTests
{
    private const string AuthenticatedDevice = "tank-000123";

    /// <summary>
    /// Deliberately different from every timestamp in the fixtures, so an assertion on it cannot
    /// pass by picking up the device's own sentAt or measuredAt instead.
    /// </summary>
    private static readonly DateTimeOffset EnqueuedAt = new(2026, 9, 14, 6, 0, 13, 250, TimeSpan.Zero);

    private readonly RecordingForwarder _forwarder = new();
    private readonly TelemetryService _service;

    public TelemetryServiceTests()
    {
        _service = new TelemetryService(_forwarder, NullLogger<TelemetryService>.Instance);
    }

    [Fact]
    public async Task ProcessAsync_MatchingIdentity_Forwards()
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, result.Outcome);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", Assert.Single(_forwarder.Forwarded).Message.MessageId);
    }

    /// <summary>
    /// The forwarder learns whose readings these are from the context, not the payload, and the
    /// receipt time it is given is the hub's, not the device's.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_Forwards_WithAuthenticatedIdentityAndEnqueuedTime()
    {
        await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        var forwarded = Assert.Single(_forwarder.Forwarded);
        Assert.Equal(AuthenticatedDevice, forwarded.Context.AuthenticatedDeviceId);
        Assert.Equal(EnqueuedAt, forwarded.Context.EnqueuedAt);
        Assert.NotEqual(forwarded.Message.SentAt, forwarded.Context.EnqueuedAt);
    }

    /// <summary>
    /// <c>deviceId</c> is optional in the body. Its absence is not a mismatch — there is simply
    /// nothing to cross-check, and the authenticated identity stands on its own.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoDeviceIdInBody_Forwards()
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, result.Outcome);
        Assert.Single(_forwarder.Forwarded);
    }

    /// <summary>
    /// The case the context exists for. Without it, a message with no <c>deviceId</c> in the body
    /// would reach the forwarder carrying readings that belong to no one.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoDeviceIdInBody_ContextStillCarriesTheAuthenticatedIdentity()
    {
        await _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt);

        var forwarded = Assert.Single(_forwarder.Forwarded);
        Assert.Null(forwarded.Message.DeviceId);
        Assert.Equal(AuthenticatedDevice, forwarded.Context.AuthenticatedDeviceId);
        Assert.Equal(EnqueuedAt, forwarded.Context.EnqueuedAt);
    }

    /// <summary>
    /// The attack the trust model exists to stop: a device with a valid certificate filing
    /// readings against a different installation. Section 4 of the contract.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_DeviceIdMismatch_IsRejectedAndNotForwarded()
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), "tank-000999", EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedIdentityMismatch, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    /// <summary>
    /// Case matters. IoT Hub device identities are case-sensitive, so treating these as equal
    /// would let one device impersonate another that differs only in casing.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_DeviceIdDiffersOnlyInCase_IsRejected()
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), "TANK-000123", EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedIdentityMismatch, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_NoAuthenticatedIdentity_IsRejected(string? authenticatedDeviceId)
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), authenticatedDeviceId, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedUnknownIdentity, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.Invalid), MemberType = typeof(ContractFixtures))]
    public async Task ProcessAsync_InvalidPayload_IsNotForwarded(string fileName)
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read(fileName), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedInvalidPayload, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    /// <summary>
    /// Backfill arrives hours late by definition. Rejecting it would discard exactly the data
    /// that an outage makes most worth keeping, so the age of <c>measuredAt</c> is never a
    /// rejection reason — and the batch is forwarded whole, per ADR-004.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_BufferedBackfill_ForwardsTheWholeBatch()
    {
        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-buffered-backfill.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, result.Outcome);

        var forwarded = Assert.Single(_forwarder.Forwarded);
        Assert.Equal(3, forwarded.Message.Measurements.Count);
        Assert.Contains(forwarded.Message.Measurements, m => m.Quality == MeasurementQuality.Uncertain);
        Assert.Equal(AuthenticatedDevice, forwarded.Context.AuthenticatedDeviceId);
        Assert.Equal(EnqueuedAt, forwarded.Context.EnqueuedAt);
    }

    /// <summary>
    /// A conforming device's message is processed even where it uses the corners of the schema: a
    /// uint32 sequence number past 2³¹, lower-case RFC 3339 letters and a leap second. Each of these
    /// used to end as RejectedInvalidPayload, so the message was dropped.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_SchemaValidEdgeCases_AreForwarded()
    {
        var payload = InlinePayload.Telemetry(
            sequence: "4294967295",
            sentAt: "2017-01-01t00:00:05z",
            measuredAt: "2016-12-31T23:59:60Z");

        var result = await _service.ProcessAsync(payload, AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, result.Outcome);
        var forwarded = Assert.Single(_forwarder.Forwarded).Message;
        Assert.Equal(4294967295L, forwarded.Sequence);
        Assert.Equal(new DateTimeOffset(2017, 1, 1, 0, 0, 5, TimeSpan.Zero), forwarded.SentAt);
        Assert.Equal(new DateTimeOffset(2016, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(TimeSpan.TicksPerSecond - 1), Assert.Single(forwarded.Measurements).MeasuredAt);
    }

    /// <summary>
    /// A value the schema accepts but the platform cannot hold is a rejection like any other: not
    /// forwarded, not thrown, and logged with the field and the reason.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_UnrepresentableValue_IsRejectedWithTheReasonLogged()
    {
        var logger = new CapturingLogger<TelemetryService>();
        var service = new TelemetryService(_forwarder, logger);

        var result = await service.ProcessAsync(
            InlinePayload.Telemetry(measuredAt: "2026-09-14T06:00:10+23:59"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedInvalidPayload, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(AuthenticatedDevice, warning.Properties["DeviceId"]);
        Assert.StartsWith(
            "/measurements/0/measuredAt: '2026-09-14T06:00:10+23:59' has a UTC offset beyond ±14:00",
            (string)warning.Properties["Reason"]!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_MalformedPayload_IsRejectedWithoutThrowing()
    {
        var result = await _service.ProcessAsync("{ not json", AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedInvalidPayload, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    public static TheoryData<string> ForwardingFailures => new()
    {
        "gateway refused",
        "no access token",
        "network failure",
        "timeout"
    };

    /// <summary>
    /// Whatever made forwarding fail, the outcome says so and the service does not throw —
    /// consistent with how rejections are handled. The log line must carry the message id, the
    /// authenticated device and the reason: if dead-lettering fails as well, it is the only record.
    /// The result carries the reason too, for the dead-letter entry (ADR-010).
    /// </summary>
    [Theory]
    [MemberData(nameof(ForwardingFailures))]
    public async Task ProcessAsync_ForwardingFails_ReturnsForwardingFailedAndLogsWhy(string failure)
    {
        var logger = new CapturingLogger<TelemetryService>();
        var service = new TelemetryService(_forwarder, logger);
        _forwarder.FailWith = failure switch
        {
            "gateway refused" => new ForwardingException("Gateway v1 responded 400 BadRequest: /readings/0/quality: missing") { StatusCode = HttpStatusCode.BadRequest },
            "no access token" => new AccessTokenException("Token endpoint responded 401 Unauthorized: invalid_client"),
            "network failure" => new HttpRequestException("Connection refused (gateway.iiot.test:443)"),
            "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        var result = await service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, result.Outcome);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5A", result.MessageId);
        Assert.Equal(_forwarder.FailWith.Message, result.FailureReason);
        Assert.Single(_forwarder.Forwarded);

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5A", error.Properties["MessageId"]);
        Assert.Equal(AuthenticatedDevice, error.Properties["DeviceId"]);
        Assert.Equal(_forwarder.FailWith.Message, error.Properties["Reason"]);
        Assert.Same(_forwarder.FailWith, error.Exception);
    }

    /// <summary>
    /// The caller cancelling — the host shutting down — is not a forwarding failure. It
    /// propagates, so the host can stop instead of logging an error for every message in flight.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_CallerCancels_PropagatesInsteadOfFailing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _forwarder.FailWith = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt, cancellation.Token));
    }

    /// <summary>
    /// A rejection decided before forwarding is still a rejection, even with a forwarder that
    /// would fail: the identity checks are unchanged and come first.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_IdentityMismatch_IsStillRejectedBeforeForwarding()
    {
        _forwarder.FailWith = new ForwardingException("should never be reached");

        var result = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), "tank-000999", EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedIdentityMismatch, result.Outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    /// <summary>
    /// The gateway's final status travels with the failure, so a dead-letter entry can say whether
    /// the gateway refused the batch or never answered.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ProcessAsync_GatewayRefuses_TheResultCarriesItsStatusCode(HttpStatusCode status)
    {
        _forwarder.FailWith = new ForwardingException($"Gateway v1 responded {(int)status} {status}") { StatusCode = status };

        var result = await _service.ProcessAsync(ContractFixtures.Read("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
    }

    /// <summary>A failure that never reached the gateway has no status code to report.</summary>
    [Fact]
    public async Task ProcessAsync_GatewayUnreachable_TheResultHasNoStatusCode()
    {
        _forwarder.FailWith = new HttpRequestException("Connection refused (gateway.iiot.test:443)");

        var result = await _service.ProcessAsync(ContractFixtures.Read("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.ForwardingFailed, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal("Connection refused (gateway.iiot.test:443)", result.FailureReason);
    }

    /// <summary>
    /// A delivered message and a rejection after validation both name the message, so a caller can
    /// log what it decided about; neither carries a failure.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_ValidatedMessages_TheResultCarriesTheMessageId()
    {
        var forwarded = await _service.ProcessAsync(ContractFixtures.Read("valid-full.json"), AuthenticatedDevice, EnqueuedAt);
        var mismatch = await _service.ProcessAsync(ContractFixtures.Read("valid-full.json"), "tank-000999", EnqueuedAt);

        Assert.Equal(new ProcessingResult(ProcessingOutcome.Forwarded, "01J8F2K9QS4T7V0X3Z6B8N1M5B"), forwarded);
        Assert.Equal(new ProcessingResult(ProcessingOutcome.RejectedIdentityMismatch, "01J8F2K9QS4T7V0X3Z6B8N1M5B"), mismatch);
    }

    /// <summary>A payload that could not be read has no message id to report.</summary>
    [Fact]
    public async Task ProcessAsync_InvalidPayload_TheResultHasNoMessageId()
    {
        var result = await _service.ProcessAsync("{ not json", AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(new ProcessingResult(ProcessingOutcome.RejectedInvalidPayload), result);
    }
}
