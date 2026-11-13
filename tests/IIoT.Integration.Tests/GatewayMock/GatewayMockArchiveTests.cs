using System.Net;
using System.Text;
using Azure;
using IIoT.GatewayMock.Readings;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The mock with an archive (ADR-012): what it accepts outlives it. A second mock built on the same
/// archive is the first one after a restart, the way App Service F1 restarts it after every idle
/// spell.
/// </summary>
public sealed class GatewayMockArchiveTests : IDisposable
{
    private const string DeviceId = "tank-archive-1";

    private readonly TestTokenIssuer _issuer = new();
    private readonly InMemoryReadingArchive _archive = new();

    /// <summary>
    /// The archive keeps the request body as it arrived, spacing included, with who delivered it;
    /// memory, which the tests and the dashboard read, has it too.
    /// </summary>
    [Fact]
    public async Task AcceptedBatch_IsArchivedByteForByte()
    {
        await using var mock = StartMock();
        var messageId = GatewayRequests.NewMessageId();
        var sent = "  " + GatewayRequests.ValidBatch(DeviceId, messageId) + "\n";

        Assert.Equal(HttpStatusCode.Accepted, await DeliverAsync(mock, sent, messageId));

        var archived = Assert.Single(_archive.Batches);
        Assert.Equal(Encoding.UTF8.GetBytes(sent), archived.Body.ToArray());
        Assert.Equal(DeviceId, archived.DeviceId);
        Assert.Equal(messageId, archived.IdempotencyKey);
        Assert.Equal(TestTokenIssuer.Subject, archived.Caller);
        Assert.Single(mock.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    /// <summary>
    /// After a restart memory is empty, and the archive still knows the key: a replay is
    /// acknowledged, and neither the archive nor memory gains a second copy.
    /// </summary>
    [Fact]
    public async Task Replay_AfterARestart_IsAcknowledged_AndTheFirstDeliveryKept()
    {
        var messageId = GatewayRequests.NewMessageId();
        var first = GatewayRequests.ValidBatch(DeviceId, messageId, "2026-11-13T06:00:00Z");
        await using (var before = StartMock())
        {
            Assert.Equal(HttpStatusCode.Accepted, await DeliverAsync(before, first, messageId));
        }

        await using var after = StartMock();
        var replay = GatewayRequests.ValidBatch(DeviceId, messageId, "2026-11-13T06:05:00Z");

        Assert.Equal(HttpStatusCode.Accepted, await DeliverAsync(after, replay, messageId));

        Assert.Equal(Encoding.UTF8.GetBytes(first), Assert.Single(_archive.Batches).Body.ToArray());
        Assert.DoesNotContain(after.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    /// <summary>
    /// A batch the archive cannot take is not accepted: <c>503</c>, so the sender keeps it, and
    /// nothing is in memory either. Delivered again once storage answers, it is stored once.
    /// Removing the catch in the endpoint makes the first delivery a 500 that stores nothing, and
    /// this fails on the status.
    /// </summary>
    [Fact]
    public async Task ArchiveUnavailable_Answers503_AndTheNextDeliveryIsStored()
    {
        await using var mock = StartMock();
        var messageId = GatewayRequests.NewMessageId();
        var body = GatewayRequests.ValidBatch(DeviceId, messageId);
        _archive.WriteFailure = new RequestFailedException(403, "This request is not authorized to perform this operation using this permission.", "AuthorizationPermissionMismatch", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await DeliverAsync(mock, body, messageId));
        Assert.Empty(_archive.Batches);
        Assert.DoesNotContain(mock.Store.Batches, batch => batch.IdempotencyKey == messageId);

        _archive.WriteFailure = null;

        Assert.Equal(HttpStatusCode.Accepted, await DeliverAsync(mock, body, messageId));
        Assert.Single(_archive.Batches);
        Assert.Single(mock.Store.Batches, batch => batch.IdempotencyKey == messageId);
    }

    /// <summary>
    /// The point of the archive: the dashboard of a restarted mock shows what the earlier one
    /// accepted, and says the messages are kept.
    /// </summary>
    [Fact]
    public async Task Dashboard_AfterARestart_ShowsWhatWasStoredBefore()
    {
        var messageId = GatewayRequests.NewMessageId();
        using (var before = new DashboardFixture(_archive))
        {
            var body = GatewayRequests.ValidBatch(DeviceId, messageId);
            using var request = GatewayRequests.PostReadings(body, before.Issuer.CreateToken(), messageId);
            using var response = await before.Factory.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        using var after = new DashboardFixture(_archive);
        var page = await OpenDashboardAsync(after, DashboardFixture.DashboardPath);

        Assert.Contains(DeviceId, page, StringComparison.Ordinal);
        Assert.Contains("Kept in blob storage", page, StringComparison.Ordinal);
        Assert.True(Assert.Single(after.Factory.Store.Batches).Sequence <= 0);
    }

    /// <summary>
    /// The archive is read once, on the first page, not at startup and not on every refresh; and
    /// what it brings back comes before what the restarted mock accepted itself, oldest first.
    /// </summary>
    [Fact]
    public async Task Restore_ReadsTheArchiveOnce_AndPutsEarlierBatchesFirst()
    {
        var now = DateTimeOffset.UtcNow;
        _archive.Add(Archived(DeviceId, "older", now.AddHours(-2)));
        _archive.Add(Archived(DeviceId, "old", now.AddHours(-1)));

        using var dashboard = new DashboardFixture(_archive);
        var live = GatewayRequests.NewMessageId();
        using (var request = GatewayRequests.PostReadings(GatewayRequests.ValidBatch(DeviceId, live), dashboard.Issuer.CreateToken(), live))
        using (var response = await dashboard.Factory.CreateClient().SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(0, _archive.Reads);

        var (browser, _) = await dashboard.SignInAsync();
        using (browser)
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(DashboardFixture.DashboardPath)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/dashboard/device?id={DeviceId}")).StatusCode);
        }

        Assert.Equal(1, _archive.Reads);
        Assert.Equal(["older", "old", live], dashboard.Factory.Store.Batches.Select(batch => batch.IdempotencyKey));
    }

    /// <summary>
    /// An archive that cannot be read does not take the dashboard down: it shows what memory holds
    /// and says that older messages are missing, and the next page tries again.
    /// </summary>
    [Fact]
    public async Task Restore_Failure_ShowsANotice_AndTheNextPageTriesAgain()
    {
        _archive.Add(Archived(DeviceId, "stored-earlier", DateTimeOffset.UtcNow.AddHours(-1)));
        _archive.ReadFailure = new RequestFailedException(503, "The server is busy.", "ServerBusy", null);

        using var dashboard = new DashboardFixture(_archive);
        var (browser, _) = await dashboard.SignInAsync();
        using (browser)
        {
            var failed = await browser.GetStringAsync(DashboardFixture.DashboardPath);
            Assert.Contains("could not be loaded", failed, StringComparison.Ordinal);
            Assert.DoesNotContain(DeviceId, failed, StringComparison.Ordinal);

            _archive.ReadFailure = null;

            var restored = await browser.GetStringAsync(DashboardFixture.DashboardPath);
            Assert.DoesNotContain("could not be loaded", restored, StringComparison.Ordinal);
            Assert.Contains(DeviceId, restored, StringComparison.Ordinal);
        }

        Assert.Equal(2, _archive.Reads);
    }

    /// <summary>A stored entry that is not JSON is skipped; the rest are restored.</summary>
    [Fact]
    public async Task Restore_SkipsAnEntryThatIsNotJson()
    {
        var now = DateTimeOffset.UtcNow;
        _archive.Add(Archived(DeviceId, "good", now.AddHours(-1)));
        _archive.Add(new ArchivedBatch("tank-archive-broken", "broken", null, now, "{ not json"u8.ToArray()));

        using var dashboard = new DashboardFixture(_archive);
        var page = await OpenDashboardAsync(dashboard, DashboardFixture.DashboardPath);

        Assert.Contains(DeviceId, page, StringComparison.Ordinal);
        Assert.DoesNotContain("tank-archive-broken", page, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded", page, StringComparison.Ordinal);
    }

    /// <summary>Without an archive the dashboard says what it always said: a restart empties it.</summary>
    [Fact]
    public async Task Dashboard_WithoutAnArchive_SaysARestartEmptiesIt()
    {
        using var dashboard = new DashboardFixture();

        var page = await OpenDashboardAsync(dashboard, DashboardFixture.DashboardPath);

        Assert.Contains("Held in memory: a restart of the mock empties it.", page, StringComparison.Ordinal);
    }

    public void Dispose() => _issuer.Dispose();

    private GatewayMockFactory StartMock() =>
        new(_issuer.GatewaySettings(), services => services.AddSingleton<IReadingArchive>(_archive));

    private async Task<HttpStatusCode> DeliverAsync(GatewayMockFactory mock, string body, string messageId)
    {
        using var client = mock.CreateClient();
        using var request = GatewayRequests.PostReadings(body, _issuer.CreateToken(), messageId);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> OpenDashboardAsync(DashboardFixture dashboard, string path)
    {
        var (browser, _) = await dashboard.SignInAsync();
        using (browser)
        {
            return await browser.GetStringAsync(path);
        }
    }

    /// <summary>A valid batch as an earlier run of the mock archived it.</summary>
    private static ArchivedBatch Archived(string deviceId, string messageId, DateTimeOffset storedAt) =>
        new(deviceId, messageId, TestTokenIssuer.Subject, storedAt,
            Encoding.UTF8.GetBytes(GatewayRequests.ValidBatch(deviceId, messageId)));
}
