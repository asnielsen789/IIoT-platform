using System.Text;
using Azure;
using IIoT.GatewayMock.Readings;
using IIoT.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IIoT.Integration.Tests.GatewayMock;

/// <summary>
/// The archive in Blob Storage (ADR-012), through the real Azure SDK, against
/// <see cref="FakeBlobService"/>: how batches are named, that a write only ever creates, and that
/// reading back returns what was written.
/// </summary>
public class BlobReadingArchiveTests
{
    private static readonly DateTimeOffset StoredAt = new(2026, 11, 13, 10, 15, 30, 123, TimeSpan.Zero);

    private readonly FakeBlobService _service = new();

    [Fact]
    public void BlobName_OfTheUsualIds_IsTheIdsThemselves()
    {
        Assert.Equal(
            "tank-000001/0f8fad5bd9cb469fa16570867728950e.json",
            BlobReadingArchive.BlobName("tank-000001", "0f8fad5bd9cb469fa16570867728950e"));
    }

    /// <summary>
    /// Whatever the IDs hold, a name has one slash, its only dot is the suffix's, and it reads back
    /// as the same pair. IoT Hub allows device IDs such as <c>..</c> and <c>a.b</c>; a <c>.</c> or
    /// <c>..</c> segment would be resolved away in the blob's URL, into another folder or container.
    /// </summary>
    [Theory]
    [InlineData("tank-000001", "m1")]
    [InlineData("..", "m1")]
    [InlineData(".", "..")]
    [InlineData("a/b", "m1")]
    [InlineData("a%2Fb", "m1")]
    [InlineData("sensor.1", "key.json")]
    [InlineData("dév", "ключ")]
    [InlineData("with space", "a:b@c")]
    [InlineData("UPPER", "lower")]
    public void BlobName_IsOneFolderPerDevice_AndReadsBack(string deviceId, string key)
    {
        var name = BlobReadingArchive.BlobName(deviceId, key);

        Assert.Single(name, character => character == '/');
        Assert.Equal(name.Length - ".json".Length, name.IndexOf('.', StringComparison.Ordinal));
        Assert.True(BlobReadingArchive.TryParseBlobName(name, out var parsedDevice, out var parsedKey));
        Assert.Equal(deviceId, parsedDevice);
        Assert.Equal(key, parsedKey);
    }

    /// <summary>Two devices never share a name, however their IDs and keys split around a slash.</summary>
    [Theory]
    [InlineData("a/b", "c", "a", "b/c")]
    [InlineData("a%2Fb", "c", "a/b", "c")]
    [InlineData("a.b", "c", "a%2Eb", "c")]
    public void BlobName_KeepsDistinctPairsApart(string device1, string key1, string device2, string key2)
    {
        Assert.NotEqual(BlobReadingArchive.BlobName(device1, key1), BlobReadingArchive.BlobName(device2, key2));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("tank-000001.json")]
    [InlineData("tank-000001/m1/extra.json")]
    [InlineData("tank-000001/m1.txt")]
    [InlineData("/m1.json")]
    [InlineData("tank-000001/.json")]
    [InlineData("sensor%2e1/m1.json")]
    [InlineData("sensor.1/m1.json")]
    public void TryParseBlobName_RefusesNamesTheArchiveDoesNotWrite(string name)
    {
        Assert.False(BlobReadingArchive.TryParseBlobName(name, out _, out _));
    }

    /// <summary>
    /// One Put Blob: create-only, the body byte for byte, the type, who delivered it and when, and a
    /// bearer token, since the account takes no keys.
    /// </summary>
    [Fact]
    public async Task TryAdd_CreatesTheBlob_ByteForByte()
    {
        var body = Encoding.UTF8.GetBytes("{ \"deviceId\": \"tank-000001\" ,\n  \"messageId\": \"m1\" }\n");
        var archive = _service.CreateArchive(NullLogger<BlobReadingArchive>.Instance);

        Assert.True(await archive.TryAddAsync(Batch("tank-000001", "m1", body, "client@clients"), CancellationToken.None));

        var put = Assert.Single(_service.Requests);
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("tank-000001/m1.json", put.BlobName);
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.Equal("BlockBlob", put.Headers["x-ms-blob-type"]);
        Assert.Equal("application/json", put.Headers["x-ms-blob-content-type"]);
        Assert.Equal("client%40clients", put.Headers["x-ms-meta-caller"]);
        Assert.Equal("2026-11-13T10:15:30.1230000+00:00", put.Headers["x-ms-meta-storedat"]);
        Assert.Equal(body, put.Body);
        Assert.Equal(body, _service.Blobs["tank-000001/m1.json"].Content);
    }

    /// <summary>The first delivery wins: a second write of the same key changes nothing and says so.</summary>
    [Fact]
    public async Task TryAdd_OfAKeyAlreadyArchived_ReturnsFalse_AndKeepsTheFirst()
    {
        var archive = _service.CreateArchive(NullLogger<BlobReadingArchive>.Instance);
        await archive.TryAddAsync(Batch("tank-000001", "m1", "{\"first\":1}"u8.ToArray()), CancellationToken.None);

        Assert.False(await archive.TryAddAsync(Batch("tank-000001", "m1", "{\"second\":2}"u8.ToArray()), CancellationToken.None));

        Assert.Equal("{\"first\":1}"u8.ToArray(), _service.Blobs["tank-000001/m1.json"].Content);
    }

    [Fact]
    public async Task TryAdd_WithoutARole_Throws()
    {
        _service.RefuseWrites = System.Net.HttpStatusCode.Forbidden;
        var archive = _service.CreateArchive(NullLogger<BlobReadingArchive>.Instance);

        var refused = await Assert.ThrowsAsync<RequestFailedException>(
            () => archive.TryAddAsync(Batch("tank-000001", "m1", "{}"u8.ToArray()), CancellationToken.None));

        Assert.Equal(403, refused.Status);
        Assert.Empty(_service.Blobs);
    }

    /// <summary>
    /// What goes in comes out, across listing pages, with its device, key, caller and time; a blob
    /// the archive did not write is left out and named in the log.
    /// </summary>
    [Fact]
    public async Task ReadAll_ReturnsEveryBatchWritten()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var archive = _service.CreateArchive(loggerFactory.CreateLogger<BlobReadingArchive>());
        _service.PageSize = 2;

        var written = Enumerable.Range(1, 5)
            .Select(i => Batch($"tank-00000{i}", $"m{i}", Encoding.UTF8.GetBytes($"{{\"n\":{i}}}"), i % 2 == 0 ? null : "client@clients"))
            .ToList();
        foreach (var batch in written)
        {
            await archive.TryAddAsync(batch, CancellationToken.None);
        }

        _service.Put("README.txt", "not a batch"u8.ToArray());

        var read = await archive.ReadAllAsync(CancellationToken.None);

        Assert.Equal(
            written.Select(Describe).Order(),
            read.Select(Describe).Order());
        Assert.True(_service.Requests.Count(request => request.Query.Contains("comp=list", StringComparison.Ordinal)) >= 3);
        Assert.Contains(logs.For<BlobReadingArchive>(), entry => entry.Message.Contains("README.txt", StringComparison.Ordinal));
    }

    /// <summary>A blob deleted between the listing and the read, by the retention rule, is simply gone.</summary>
    [Fact]
    public async Task ReadAll_SkipsABlobGoneSinceTheListing()
    {
        var archive = _service.CreateArchive(NullLogger<BlobReadingArchive>.Instance);
        await archive.TryAddAsync(Batch("tank-000001", "kept", "{}"u8.ToArray()), CancellationToken.None);
        await archive.TryAddAsync(Batch("tank-000001", "expired", "{}"u8.ToArray()), CancellationToken.None);
        _service.GoneWhenRead["tank-000001/expired.json"] = true;

        var read = await archive.ReadAllAsync(CancellationToken.None);

        Assert.Equal("kept", Assert.Single(read).IdempotencyKey);
    }

    private static ArchivedBatch Batch(string deviceId, string key, byte[] body, string? caller = null) =>
        new(deviceId, key, caller, StoredAt, body);

    private static string Describe(ArchivedBatch batch) =>
        $"{batch.DeviceId}|{batch.IdempotencyKey}|{batch.Caller}|{batch.StoredAt:O}|{Encoding.UTF8.GetString(batch.Body.Span)}";
}
