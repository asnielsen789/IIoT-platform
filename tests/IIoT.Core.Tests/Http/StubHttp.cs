using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace IIoT.Core.Tests.Http;

/// <summary>
/// Answers HTTP requests in-process and records what was sent. No socket is ever opened.
/// </summary>
/// <remarks>
/// The body is read when the request is sent, because the sender disposes the request afterwards
/// and an assertion made later could no longer read it.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    public StubHttpMessageHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public StubHttpMessageHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.MediaType,
            body);

        lock (_gate)
        {
            _requests.Add(recorded);
        }

        return await _respond(recorded, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>One request as the stub saw it.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    AuthenticationHeaderValue? Authorization,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    string? Body)
{
    /// <summary>The body parsed as <c>application/x-www-form-urlencoded</c>.</summary>
    public IReadOnlyDictionary<string, string> Form() =>
        (Body ?? "")
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "");
}

/// <summary>Hands out clients over one stub handler, and remembers which names were asked for.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public List<string> RequestedNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        RequestedNames.Add(name);
        return new HttpClient(_handler, disposeHandler: false);
    }
}
