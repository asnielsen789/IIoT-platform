using System.Net;
using IIoT.Integration.Tests.Infrastructure;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// Delivery is at least once — the event stream redelivers after a restart, and the platform
/// retries a request whose response it never saw — so a replay is normal. The mock has to
/// acknowledge it with the same status and store nothing twice.
/// </summary>
public class GatewayMockIdempotencyTests : IClassFixture<GatewayMockFixture>
{
    private readonly GatewayMockFixture _gateway;

    public GatewayMockIdempotencyTests(GatewayMockFixture gateway)
    {
        _gateway = gateway;
    }

    [Fact]
    public async Task Replay_IsAcceptedWithTheSameStatus_AndStoredOnce()
    {
        var messageId = GatewayRequests.NewMessageId();
        var body = GatewayRequests.ValidBatch("tank-000123", messageId);

        var first = await PostAsync(body, messageId);
        var replay = await PostAsync(body, messageId);

        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal(first, replay);
        Assert.Single(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    /// <summary>
    /// A device that resends a message gets a new enqueued time, so the replay's body differs in
    /// <c>receivedAt</c>. It is still the same message: the first delivery wins, and the replay is
    /// acknowledged rather than refused.
    /// </summary>
    [Fact]
    public async Task Replay_WithALaterReceivedAt_KeepsTheFirstDelivery()
    {
        var messageId = GatewayRequests.NewMessageId();

        var first = await PostAsync(GatewayRequests.ValidBatch("tank-000123", messageId, "2026-09-14T06:00:13Z"), messageId);
        var resend = await PostAsync(GatewayRequests.ValidBatch("tank-000123", messageId, "2026-09-14T06:05:41Z"), messageId);

        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal(HttpStatusCode.Accepted, resend);

        var stored = Assert.Single(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == messageId);
        Assert.Equal("2026-09-14T06:00:13Z", stored.Body.GetProperty("receivedAt").GetString());
    }

    /// <summary>
    /// The device contract promises a message id is unique per device, not across the fleet.
    /// Two devices that happen to use the same id each get their batch stored — otherwise one
    /// device's data would vanish as a "duplicate" of another's.
    /// </summary>
    [Fact]
    public async Task SameMessageId_FromTwoDevices_IsStoredForEach()
    {
        var messageId = GatewayRequests.NewMessageId();

        var first = await PostAsync(GatewayRequests.ValidBatch("tank-000001", messageId), messageId);
        var second = await PostAsync(GatewayRequests.ValidBatch("tank-000002", messageId), messageId);

        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal(HttpStatusCode.Accepted, second);

        var stored = _gateway.Factory.Store.Batches.Where(batch => batch.IdempotencyKey == messageId).ToList();
        Assert.Equal(
            new[] { "tank-000001", "tank-000002" },
            stored.Select(batch => batch.DeviceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MissingIdempotencyKey_IsBadRequest()
    {
        var messageId = GatewayRequests.NewMessageId();

        var status = await PostAsync(GatewayRequests.ValidBatch("tank-000123", messageId), idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(_gateway.Factory.Store.Batches, batch => batch.DeviceId == "tank-000123" && batch.Body.GetProperty("messageId").GetString() == messageId);
    }

    [Fact]
    public async Task EmptyIdempotencyKey_IsBadRequest()
    {
        var messageId = GatewayRequests.NewMessageId();

        var status = await PostAsync(GatewayRequests.ValidBatch("tank-000123", messageId), idempotencyKey: " ");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// The key and the body must name the same message. If they could differ, a replay check on
    /// the key would say nothing about which message was actually stored.
    /// </summary>
    [Fact]
    public async Task IdempotencyKeyThatIsNotTheMessageId_IsBadRequest()
    {
        var messageId = GatewayRequests.NewMessageId();
        var otherKey = GatewayRequests.NewMessageId();

        var status = await PostAsync(GatewayRequests.ValidBatch("tank-000123", messageId), otherKey);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(_gateway.Factory.Store.Batches, batch => batch.IdempotencyKey == otherKey);
    }

    private async Task<HttpStatusCode> PostAsync(string body, string? idempotencyKey)
    {
        using var request = GatewayRequests.PostReadings(body, _gateway.Issuer.CreateToken(), idempotencyKey);
        using var response = await _gateway.Client.SendAsync(request);
        return response.StatusCode;
    }
}
