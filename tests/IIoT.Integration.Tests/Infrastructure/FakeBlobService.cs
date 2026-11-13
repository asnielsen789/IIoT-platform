using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using System.Xml.Linq;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using IIoT.GatewayMock.Readings;
using Microsoft.Extensions.Logging;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// One blob container, answering the four requests the archive makes — Put Blob, Get Blob, List
/// Blobs and the token it is sent with — the way the Blob service documents them. It sits under the
/// Azure SDK as its HTTP transport, so the archive is tested through the real client, and what goes
/// over the wire (the create-only condition, the metadata, the body) is what a test checks.
/// </summary>
public sealed class FakeBlobService : HttpMessageHandler
{
    public const string Container = "readings";

    public static readonly Uri ContainerUri = new($"https://archive.blob.core.windows.net/{Container}");

    private static readonly DateTimeOffset Created = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentDictionary<string, FakeBlob> _blobs = new(StringComparer.Ordinal);

    /// <summary>Every request, in order.</summary>
    public ConcurrentQueue<RecordedBlobRequest> Requests { get; } = new();

    /// <summary>The blobs, by name.</summary>
    public IReadOnlyDictionary<string, FakeBlob> Blobs => _blobs;

    /// <summary>At most this many blobs per listing page; the rest follow a continuation marker.</summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>When set, every write is refused with it, as when the identity has no role.</summary>
    public HttpStatusCode? RefuseWrites { get; set; }

    /// <summary>Blobs that are listed but gone when read, as if deleted in between.</summary>
    public ConcurrentDictionary<string, bool> GoneWhenRead { get; } = new(StringComparer.Ordinal);

    /// <summary>The archive, wired to this service through the SDK with the production client settings.</summary>
    internal BlobReadingArchive CreateArchive(ILogger<BlobReadingArchive> logger)
    {
        var options = BlobReadingArchive.ClientOptions();
        options.Transport = new HttpClientTransport(this);

        var container = BlobReadingArchive.CreateContainerClient(
            new ReadingArchiveOptions { ContainerUri = ContainerUri.ToString() }, new StaticTokenCredential(), options);
        return new BlobReadingArchive(container, logger);
    }

    /// <summary>Puts a blob in the container directly, as something other than the archive would.</summary>
    public void Put(string name, byte[] content, Dictionary<string, string>? metadata = null) =>
        _blobs[name] = new FakeBlob(content, "application/json", metadata ?? []);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(uri.Query);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        // The path is /<container>[/<blob name, escaped once>].
        var path = uri.AbsolutePath;
        var name = path.Length > Container.Length + 2 ? Uri.UnescapeDataString(path[(Container.Length + 2)..]) : null;

        Requests.Enqueue(new RecordedBlobRequest(
            request.Method,
            name,
            uri.Query,
            request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            body));

        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: StaticTokenCredential.Token })
        {
            return Error(HttpStatusCode.Unauthorized, "NoAuthenticationInformation");
        }

        if (request.Method == HttpMethod.Get && name is null && query["restype"] == "container" && query["comp"] == "list")
        {
            return List(query["marker"], query["include"]);
        }

        if (request.Method == HttpMethod.Put && name is not null)
        {
            return Put(request, name, body);
        }

        if (request.Method == HttpMethod.Get && name is not null)
        {
            return Get(name);
        }

        return Error(HttpStatusCode.BadRequest, "UnsupportedHttpVerb");
    }

    private HttpResponseMessage Put(HttpRequestMessage request, string name, byte[] body)
    {
        if (RefuseWrites is { } status)
        {
            return Error(status, status == HttpStatusCode.Forbidden ? "AuthorizationPermissionMismatch" : "InternalError");
        }

        var metadata = request.Headers
            .Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key["x-ms-meta-".Length..], header => header.Value.Single(), StringComparer.OrdinalIgnoreCase);
        var blob = new FakeBlob(body, request.Headers.TryGetValues("x-ms-blob-content-type", out var type) ? type.Single() : "application/octet-stream", metadata);

        var createOnly = request.Headers.TryGetValues("If-None-Match", out var match) && match.Single() == "*";
        if (createOnly ? !_blobs.TryAdd(name, blob) : !Replace(name, blob))
        {
            return Error(HttpStatusCode.Conflict, "BlobAlreadyExists");
        }

        var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
        response.Headers.ETag = new("\"0x8DE000000000001\"");
        response.Content.Headers.LastModified = Created;
        return response;
    }

    private bool Replace(string name, FakeBlob blob)
    {
        _blobs[name] = blob;
        return true;
    }

    private HttpResponseMessage Get(string name)
    {
        if (!_blobs.TryGetValue(name, out var blob) || GoneWhenRead.ContainsKey(name))
        {
            return Error(HttpStatusCode.NotFound, "BlobNotFound");
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob.Content) };
        response.Content.Headers.ContentType = new(blob.ContentType);
        response.Content.Headers.LastModified = Created;
        response.Headers.ETag = new("\"0x8DE000000000001\"");
        response.Headers.Add("x-ms-blob-type", "BlockBlob");
        response.Headers.Add("x-ms-creation-time", Created.ToString("R", CultureInfo.InvariantCulture));
        foreach (var (key, value) in blob.Metadata)
        {
            response.Headers.Add("x-ms-meta-" + key, value);
        }

        return response;
    }

    /// <summary>Names in lexicographic order, as the service lists them, a page at a time.</summary>
    private HttpResponseMessage List(string? marker, string? include)
    {
        var names = _blobs.Keys.Order(StringComparer.Ordinal).ToList();
        var start = marker is null ? 0 : int.Parse(marker, CultureInfo.InvariantCulture);
        var page = names.Skip(start).Take(PageSize).ToList();
        var next = start + page.Count < names.Count ? (start + page.Count).ToString(CultureInfo.InvariantCulture) : "";
        // The SDK spells it "Metadata"; the service takes either case.
        var withMetadata = include?.Split(',').Contains("metadata", StringComparer.OrdinalIgnoreCase) == true;

        var document = new XDocument(
            new XElement("EnumerationResults",
                new XAttribute("ServiceEndpoint", "https://archive.blob.core.windows.net/"),
                new XAttribute("ContainerName", Container),
                new XElement("Blobs", page.Select(name => new XElement("Blob",
                    new XElement("Name", name),
                    new XElement("Properties",
                        new XElement("Creation-Time", Created.ToString("R", CultureInfo.InvariantCulture)),
                        new XElement("Last-Modified", Created.ToString("R", CultureInfo.InvariantCulture)),
                        new XElement("Etag", "0x8DE000000000001"),
                        new XElement("Content-Length", _blobs[name].Content.Length),
                        new XElement("Content-Type", _blobs[name].ContentType),
                        new XElement("BlobType", "BlockBlob")),
                    withMetadata ? new XElement("Metadata", _blobs[name].Metadata.Select(pair => new XElement(pair.Key, pair.Value))) : null))),
                new XElement("NextMarker", next)));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(document.Declaration + document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml")
        };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>", Encoding.UTF8, "application/xml")
        };
        response.Headers.Add("x-ms-error-code", code);
        return response;
    }

    /// <summary>A token for the fake account; the service refuses requests without it.</summary>
    private sealed class StaticTokenCredential : TokenCredential
    {
        public const string Token = "archive-test-token";

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(Token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

public sealed record FakeBlob(byte[] Content, string ContentType, IReadOnlyDictionary<string, string> Metadata);

/// <param name="BlobName">The blob the request is for, unescaped; <c>null</c> for the container.</param>
public sealed record RecordedBlobRequest(
    HttpMethod Method,
    string? BlobName,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);
