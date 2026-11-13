using System.Collections.Concurrent;
using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace IIoT.GatewayMock.Readings;

/// <summary>
/// The archive in an Azure Blob Storage container (ADR-012): one block blob per accepted batch,
/// its content the request body byte for byte, named <c>&lt;device&gt;/&lt;key&gt;.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Plain JSON files in a container are the hand-over format: an operator can copy them with any
/// tool, replay one with <c>curl</c>, or point another system at them. Nothing here is specific to
/// the mock beyond the two metadata entries, which say who delivered the batch and when.
/// </para>
/// <para>
/// A write is a create: <c>If-None-Match: *</c> makes the service refuse it when the blob exists,
/// so the first delivery wins even between two instances racing on one key, and a replay after a
/// restart is recognised without anything in memory. Retention is the container's business: a
/// lifecycle rule deletes blobs once they are older than the retention period
/// (<c>infra/modules/storage.bicep</c>).
/// </para>
/// </remarks>
internal sealed class BlobReadingArchive : IReadingArchive
{
    /// <summary>Who delivered the batch: the token's <c>sub</c>, percent-encoded, since metadata is ASCII.</summary>
    internal const string CallerMetadata = "caller";

    /// <summary>When the mock accepted the batch, in the round-trip format.</summary>
    internal const string StoredAtMetadata = "storedat";

    private const string Suffix = ".json";

    /// <summary>
    /// Downloads in flight while reading the archive back. The blobs are small, so the time is in
    /// the round trips, and a month of the reference fleet's traffic is a few thousand of them.
    /// </summary>
    private const int ParallelDownloads = 16;

    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobReadingArchive> _logger;

    public BlobReadingArchive(IOptions<ReadingArchiveOptions> options, ILogger<BlobReadingArchive> logger)
        : this(CreateContainerClient(options.Value, CreateCredential(options.Value), ClientOptions()), logger)
    {
    }

    internal BlobReadingArchive(BlobContainerClient container, ILogger<BlobReadingArchive> logger)
    {
        _container = container;
        _logger = logger;
    }

    public async Task<bool> TryAddAsync(ArchivedBatch batch, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            [StoredAtMetadata] = batch.StoredAt.ToString("O", CultureInfo.InvariantCulture)
        };

        if (batch.Caller is not null)
        {
            metadata[CallerMetadata] = Uri.EscapeDataString(batch.Caller);
        }

        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            Metadata = metadata
        };

        try
        {
            await _container.GetBlobClient(BlobName(batch.DeviceId, batch.IdempotencyKey))
                .UploadAsync(BinaryData.FromBytes(batch.Body), options, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (IsAlreadyThere(ex))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ArchivedBatch>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var items = new List<BlobItem>();
        await foreach (var item in _container
            .GetBlobsAsync(new GetBlobsOptions { Traits = BlobTraits.Metadata }, cancellationToken)
            .ConfigureAwait(false))
        {
            items.Add(item);
        }

        var batches = new ConcurrentBag<ArchivedBatch>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = ParallelDownloads, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(items, parallel, async (item, token) =>
        {
            if (!TryParseBlobName(item.Name, out var deviceId, out var idempotencyKey))
            {
                _logger.LogWarning("Skipped blob {BlobName}: not a name the archive writes", item.Name);
                return;
            }

            BinaryData content;
            try
            {
                content = (await _container.GetBlobClient(item.Name).DownloadContentAsync(token).ConfigureAwait(false)).Value.Content;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Deleted since the listing, by the retention rule or by hand: no longer archived.
                return;
            }

            batches.Add(new ArchivedBatch(
                deviceId,
                idempotencyKey,
                Caller(item.Metadata),
                StoredAt(item),
                content.ToMemory()));
        }).ConfigureAwait(false);

        return [.. batches];
    }

    /// <summary>
    /// The blob that holds a device's batch. Each part is percent-encoded, dots included, so the one
    /// slash is the separator, no device can name another device's folder, and no part is a <c>.</c>
    /// or <c>..</c> segment, which a URL would resolve away. The usual IDs, such as
    /// <c>tank-000001</c> and a GUID, are unchanged.
    /// </summary>
    internal static string BlobName(string deviceId, string idempotencyKey) =>
        $"{Encode(deviceId)}/{Encode(idempotencyKey)}{Suffix}";

    /// <summary>
    /// The device and key a blob holds, for names this archive writes; any other blob in the
    /// container is left alone.
    /// </summary>
    internal static bool TryParseBlobName(string name, out string deviceId, out string idempotencyKey)
    {
        deviceId = idempotencyKey = "";

        if (!name.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name[..^Suffix.Length].Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        var device = Uri.UnescapeDataString(parts[0]);
        var key = Uri.UnescapeDataString(parts[1]);

        // Only the one spelling this archive writes: a name with a lower-case escape, or with a
        // character it would have escaped, is somebody else's.
        if (!string.Equals(BlobName(device, key), name, StringComparison.Ordinal))
        {
            return false;
        }

        (deviceId, idempotencyKey) = (device, key);
        return true;
    }

    /// <summary>
    /// The client settings, apart from the transport, which a test replaces. Shorter than the
    /// Storage defaults (up to five retries with a 100-second timeout each): the platform waits for
    /// the answer, and retries a delivery the mock could not store.
    /// </summary>
    internal static BlobClientOptions ClientOptions()
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 3;
        options.Retry.Delay = TimeSpan.FromMilliseconds(500);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(4);
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        return options;
    }

    internal static BlobContainerClient CreateContainerClient(
        ReadingArchiveOptions options, TokenCredential credential, BlobClientOptions clientOptions) =>
        new(new Uri(options.ContainerUri!), credential, clientOptions);

    private static TokenCredential CreateCredential(ReadingArchiveOptions options) =>
        string.Equals(options.Credential, ReadingArchiveOptions.ManagedIdentity, StringComparison.OrdinalIgnoreCase)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new DefaultAzureCredential();

    /// <remarks>
    /// The service answers a create that finds the blob with <c>409 BlobAlreadyExists</c>. A
    /// <c>412</c> can only mean the same here, since <c>If-None-Match: *</c> is the request's one
    /// condition, so it counts too.
    /// </remarks>
    private static bool IsAlreadyThere(RequestFailedException ex) =>
        (ex.Status == 409 && ex.ErrorCode == BlobErrorCode.BlobAlreadyExists) ||
        (ex.Status == 412 && ex.ErrorCode == BlobErrorCode.ConditionNotMet);

    private static string Encode(string value) =>
        Uri.EscapeDataString(value).Replace(".", "%2E", StringComparison.Ordinal);

    private static string? Caller(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(CallerMetadata, out var caller)
            ? Uri.UnescapeDataString(caller)
            : null;

    /// <summary>The time the mock recorded, or, for a blob without it, when the blob was created.</summary>
    private static DateTimeOffset StoredAt(BlobItem item) =>
        item.Metadata is not null &&
        item.Metadata.TryGetValue(StoredAtMetadata, out var text) &&
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var storedAt)
            ? storedAt
            : item.Properties.CreatedOn ?? DateTimeOffset.MinValue;
}
