using IIoT.Core.Models;
using IIoT.Core.Services;
using IIoT.Core.Tests.Fixtures;
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
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
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
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
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
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), "tank-000999", EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedIdentityMismatch, outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    /// <summary>
    /// Case matters. IoT Hub device identities are case-sensitive, so treating these as equal
    /// would let one device impersonate another that differs only in casing.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_DeviceIdDiffersOnlyInCase_IsRejected()
    {
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), "TANK-000123", EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedIdentityMismatch, outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_NoAuthenticatedIdentity_IsRejected(string? authenticatedDeviceId)
    {
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), authenticatedDeviceId, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedUnknownIdentity, outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.Invalid), MemberType = typeof(ContractFixtures))]
    public async Task ProcessAsync_InvalidPayload_IsNotForwarded(string fileName)
    {
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read(fileName), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedInvalidPayload, outcome);
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
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-buffered-backfill.json"), AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);

        var forwarded = Assert.Single(_forwarder.Forwarded);
        Assert.Equal(3, forwarded.Message.Measurements.Count);
        Assert.Contains(forwarded.Message.Measurements, m => m.Quality == MeasurementQuality.Uncertain);
        Assert.Equal(AuthenticatedDevice, forwarded.Context.AuthenticatedDeviceId);
        Assert.Equal(EnqueuedAt, forwarded.Context.EnqueuedAt);
    }

    [Fact]
    public async Task ProcessAsync_MalformedPayload_IsRejectedWithoutThrowing()
    {
        var outcome = await _service.ProcessAsync("{ not json", AuthenticatedDevice, EnqueuedAt);

        Assert.Equal(ProcessingOutcome.RejectedInvalidPayload, outcome);
        Assert.Empty(_forwarder.Forwarded);
    }
}
