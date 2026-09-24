using IIoT.Core.Models;
using IIoT.Core.Services;
using IIoT.Core.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace IIoT.Core.Tests.Services;

public class TelemetryServiceTests
{
    private const string AuthenticatedDevice = "tank-000123";

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
            ContractFixtures.Read("valid-full.json"), AuthenticatedDevice);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Equal("01J8F2K9QS4T7V0X3Z6B8N1M5B", Assert.Single(_forwarder.Forwarded).MessageId);
    }

    /// <summary>
    /// <c>deviceId</c> is optional in the body. Its absence is not a mismatch — there is simply
    /// nothing to cross-check, and the authenticated identity stands on its own.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoDeviceIdInBody_Forwards()
    {
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-minimal.json"), AuthenticatedDevice);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);
        Assert.Single(_forwarder.Forwarded);
    }

    /// <summary>
    /// The attack the trust model exists to stop: a device with a valid certificate filing
    /// readings against a different installation. Section 4 of the contract.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_DeviceIdMismatch_IsRejectedAndNotForwarded()
    {
        var outcome = await _service.ProcessAsync(
            ContractFixtures.Read("valid-full.json"), "tank-000999");

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
            ContractFixtures.Read("valid-full.json"), "TANK-000123");

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
            ContractFixtures.Read("valid-minimal.json"), authenticatedDeviceId);

        Assert.Equal(ProcessingOutcome.RejectedUnknownIdentity, outcome);
        Assert.Empty(_forwarder.Forwarded);
    }

    [Theory]
    [MemberData(nameof(ContractFixtures.Invalid), MemberType = typeof(ContractFixtures))]
    public async Task ProcessAsync_InvalidPayload_IsNotForwarded(string fileName)
    {
        var outcome = await _service.ProcessAsync(ContractFixtures.Read(fileName), AuthenticatedDevice);

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
            ContractFixtures.Read("valid-buffered-backfill.json"), AuthenticatedDevice);

        Assert.Equal(ProcessingOutcome.Forwarded, outcome);

        var forwarded = Assert.Single(_forwarder.Forwarded);
        Assert.Equal(3, forwarded.Measurements.Count);
        Assert.Contains(forwarded.Measurements, m => m.Quality == MeasurementQuality.Uncertain);
    }

    [Fact]
    public async Task ProcessAsync_MalformedPayload_IsRejectedWithoutThrowing()
    {
        var outcome = await _service.ProcessAsync("{ not json", AuthenticatedDevice);

        Assert.Equal(ProcessingOutcome.RejectedInvalidPayload, outcome);
        Assert.Empty(_forwarder.Forwarded);
    }
}
