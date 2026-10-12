using System.Net;
using System.Text;
using IIoT.Core.Authentication;
using IIoT.Core.Tests.Http;
using IIoT.Core.Tests.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IIoT.Core.Tests.Authentication;

public class ClientCredentialsTokenProviderTests
{
    private const string ClientId = "iiot-platform";
    private const string Audience = "https://gateway.iiot.test";
    private static readonly Uri TokenEndpoint = new("https://issuer.iiot.test/oauth/token");

    /// <summary>
    /// Generated per test run, never stored. Distinctive enough that finding it in a log line
    /// cannot be a coincidence.
    /// </summary>
    private readonly string _clientSecret = "secret-" + Guid.NewGuid().ToString("N");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger<ClientCredentialsTokenProvider> _logger = new();
    private int _issued;

    [Fact]
    public async Task GetAccessToken_PostsAClientCredentialsGrant()
    {
        var (provider, handler) = Create(IssueTokens());

        var token = await provider.GetAccessTokenAsync();

        Assert.Equal("token-1", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TokenEndpoint, request.Uri);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Null(request.Authorization);

        var form = request.Form();
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal(_clientSecret, form["client_secret"]);
        Assert.Equal(Audience, form["audience"]);
        Assert.False(form.ContainsKey("scope"));
    }

    /// <summary>
    /// The other authentication method RFC 6749 defines, and the one every server must support:
    /// the credentials move to the Authorization header and are no longer in the body.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_WithBasicAuthentication_SendsCredentialsInTheHeaderOnly()
    {
        var (provider, handler) = Create(IssueTokens(), options =>
        {
            options.ClientAuthentication = ClientAuthenticationMethod.ClientSecretBasic;
            options.ClientId = "client id";
            options.ClientSecret = "pa:ss word";
        });

        await provider.GetAccessTokenAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Basic", request.Authorization!.Scheme);

        // Form-urlencoded before joining (RFC 6749 section 2.3.1), so the colon in the secret
        // cannot be read as the separator.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Authorization.Parameter!));
        Assert.Equal("client+id:pa%3ass+word", decoded, ignoreCase: true);

        var form = request.Form();
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));
    }

    /// <summary>Providers that identify the API by scope rather than audience.</summary>
    [Fact]
    public async Task GetAccessToken_SendsScope_AndOmitsAnUnsetAudience()
    {
        var (provider, handler) = Create(IssueTokens(), options =>
        {
            options.Audience = null;
            options.Scope = "api://gateway/.default";
        });

        await provider.GetAccessTokenAsync();

        var form = Assert.Single(handler.Requests).Form();
        Assert.Equal("api://gateway/.default", form["scope"]);
        Assert.False(form.ContainsKey("audience"));
    }

    [Fact]
    public async Task GetAccessToken_CachesTheToken()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: 3600));

        var first = await provider.GetAccessTokenAsync();
        _time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.GetAccessTokenAsync();

        Assert.Equal(first, second);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// Renewed a margin before expiry, not at it, so a token never expires between being handed
    /// out and being checked by the receiver.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_RenewsTheMarginBeforeExpiry()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: 3600), options => options.RenewalMargin = TimeSpan.FromMinutes(5));

        Assert.Equal("token-1", await provider.GetAccessTokenAsync());

        _time.Advance(TimeSpan.FromMinutes(55) - TimeSpan.FromSeconds(1));
        Assert.Equal("token-1", await provider.GetAccessTokenAsync());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("token-2", await provider.GetAccessTokenAsync());

        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// A token that lives less than twice the margin would otherwise be "due for renewal" the
    /// moment it arrived, and fetched again for every single call.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_ShortLivedToken_IsRenewedAtHalfItsLifetime()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: 120), options => options.RenewalMargin = TimeSpan.FromMinutes(5));

        await provider.GetAccessTokenAsync();
        _time.Advance(TimeSpan.FromSeconds(59));
        await provider.GetAccessTokenAsync();
        Assert.Single(handler.Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        await provider.GetAccessTokenAsync();
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// <c>expires_in</c> is only RECOMMENDED by the RFC. Without it there is no safe way to know
    /// how long the token lives, so it is used once rather than guessed at.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_WithoutExpiresIn_DoesNotCache()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: null));

        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        Assert.Equal("token-2", await provider.GetAccessTokenAsync());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("expires_in", StringComparison.Ordinal));
    }

    /// <summary>
    /// A burst of callers arriving while no fresh token exists shares one request to the token
    /// endpoint, instead of each fetching its own and tripping the provider's rate limit.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_ConcurrentCallers_ShareOneRequest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (provider, handler) = Create(async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return TokenResponse($"token-{Interlocked.Increment(ref _issued)}", 3600);
        });

        var callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await provider.GetAccessTokenAsync()))
            .ToList();

        // Let every caller reach the provider while the first request is still outstanding.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        release.SetResult();

        var tokens = await Task.WhenAll(callers);

        Assert.Single(handler.Requests);
        Assert.All(tokens, token => Assert.Equal("token-1", token));
    }

    [Fact]
    public async Task GetAccessToken_ConcurrentCallersAfterExpiry_ShareOneRefresh()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: 3600));
        await provider.GetAccessTokenAsync();
        _time.Advance(TimeSpan.FromHours(2));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await provider.GetAccessTokenAsync())));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(tokens, token => Assert.Equal("token-2", token));
    }

    [Fact]
    public async Task Invalidate_DiscardsTheCachedToken()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: 3600));
        var rejected = await provider.GetAccessTokenAsync();

        provider.Invalidate(rejected);

        Assert.Equal("token-2", await provider.GetAccessTokenAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// A late report about an older token must not throw away the newer one that replaced it.
    /// </summary>
    [Fact]
    public async Task Invalidate_WithAnOlderToken_KeepsTheCurrentOne()
    {
        var (provider, handler) = Create(IssueTokens(expiresIn: 3600));
        var old = await provider.GetAccessTokenAsync();
        provider.Invalidate(old);
        var current = await provider.GetAccessTokenAsync();

        provider.Invalidate(old);

        Assert.Equal(current, await provider.GetAccessTokenAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetAccessToken_ErrorResponse_ThrowsWithStatusAndOAuthError()
    {
        var (provider, _) = Create(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.Unauthorized,
            """{ "error": "access_denied", "error_description": "Unauthorized" }"""));

        var exception = await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("access_denied", exception.Message, StringComparison.Ordinal);
        Assert.Contains("https://issuer.iiot.test/oauth/token", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_clientSecret, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The raw body is never quoted. Only the RFC's own error fields are, because a server is
    /// free to echo back what it was sent — including the secret.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_ErrorBodyEchoingTheRequest_IsNotQuoted()
    {
        var (provider, _) = Create(request => StubHttpMessageHandler.Json(
            HttpStatusCode.BadRequest, $$"""{ "received": "{{request.Body}}" }"""));

        var exception = await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_clientSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccessToken_AfterAFailure_TriesAgainOnTheNextCall()
    {
        var calls = 0;
        var (provider, handler) = Create(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : TokenResponse("token-after-outage", 3600));

        await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.Equal("token-after-outage", await provider.GetAccessTokenAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    public static TheoryData<string, string> MalformedResponses => new()
    {
        { """{ "token_type": "Bearer", "expires_in": 3600 }""", "without an access_token" },
        { """{ "access_token": "", "token_type": "Bearer", "expires_in": 3600 }""", "without an access_token" },
        { """{ "access_token": "abc", "expires_in": 3600 }""", "did not issue a Bearer token" },
        { """{ "access_token": "abc", "token_type": "MAC", "expires_in": 3600 }""", "did not issue a Bearer token" },
        { "<html>maintenance</html>", "not JSON" },
        { "[]", "without an access_token" }
    };

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task GetAccessToken_MalformedResponse_Throws(string body, string expected)
    {
        var (provider, _) = Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, body));

        var exception = await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>RFC 6749 section 7.1: the token type is case-insensitive.</summary>
    [Fact]
    public async Task GetAccessToken_AcceptsBearerInAnyCase()
    {
        var (provider, _) = Create(_ => StubHttpMessageHandler.Json(
            HttpStatusCode.OK, """{ "access_token": "abc", "token_type": "bearer", "expires_in": 3600 }"""));

        Assert.Equal("abc", await provider.GetAccessTokenAsync());
    }

    public static TheoryData<string, string?, string?, string> Misconfigurations => new()
    {
        { "no endpoint", null, "secret", "TokenEndpoint is not configured" },
        { "plain HTTP endpoint", "http://issuer.iiot.test/oauth/token", "secret", "TokenEndpoint must use https" },
        { "relative endpoint", "/oauth/token", "secret", "TokenEndpoint must be an absolute URL" },
        { "no secret", "https://issuer.iiot.test/oauth/token", null, "ClientSecret is not configured" },
        { "blank secret", "https://issuer.iiot.test/oauth/token", "  ", "ClientSecret is not configured" }
    };

    /// <summary>
    /// A misconfigured client sends nothing — above all not its secret to a plain-HTTP address.
    /// </summary>
    [Theory]
    [MemberData(nameof(Misconfigurations))]
    public async Task GetAccessToken_Misconfigured_ThrowsWithoutSendingAnything(
        string description, string? endpoint, string? secret, string expected)
    {
        var (provider, handler) = Create(IssueTokens(), options =>
        {
            // Explicit kinds: on Linux, RelativeOrAbsolute reads "/oauth/token" as a file path
            // and makes it an absolute file:// URI.
            options.TokenEndpoint = endpoint is null
                ? null
                : new Uri(endpoint, endpoint.Contains("://", StringComparison.Ordinal) ? UriKind.Absolute : UriKind.Relative);
            options.ClientSecret = secret;
        });

        var exception = await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.True(handler.Requests.Count == 0, $"{description}: a request was sent");
    }

    [Fact]
    public async Task GetAccessToken_NoClientId_ThrowsWithoutSendingAnything()
    {
        var (provider, handler) = Create(IssueTokens(), options => options.ClientId = null);

        var exception = await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.Contains("ClientId is not configured", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetAccessToken_PlainHttpToLoopback_IsAllowed()
    {
        var (provider, handler) = Create(IssueTokens(), options => options.TokenEndpoint = new Uri("http://localhost:8080/oauth/token"));

        await provider.GetAccessTokenAsync();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAccessToken_NetworkFailure_IsReportedAsAccessTokenException()
    {
        var (provider, _) = Create((_, _) => throw new HttpRequestException("Connection refused (issuer.iiot.test:443)"));

        var exception = await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.Contains("Connection refused", exception.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    /// <summary>
    /// The caller cancelling is not a token failure. It surfaces as cancellation, so the host can
    /// shut down cleanly instead of logging an error per message in flight.
    /// </summary>
    [Fact]
    public async Task GetAccessToken_CallerCancels_IsCancellationNotFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var (provider, _) = Create(async (_, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return TokenResponse("never", 3600);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAccessTokenAsync(cancellation.Token).AsTask());
    }

    /// <summary>
    /// Through success, failure and the no-expiry warning, neither the secret nor a token appears
    /// in any log entry.
    /// </summary>
    [Fact]
    public async Task NeitherTheSecretNorTheToken_IsEverLogged()
    {
        var calls = 0;
        var (provider, _) = Create(_ => Interlocked.Increment(ref calls) switch
        {
            1 => TokenResponse("issued-token-one", 3600),
            2 => TokenResponse("issued-token-two", null),
            _ => StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{ "error": "invalid_client" }""")
        });

        await provider.GetAccessTokenAsync();
        provider.Invalidate("issued-token-one");
        await provider.GetAccessTokenAsync();
        await Assert.ThrowsAsync<AccessTokenException>(() => provider.GetAccessTokenAsync().AsTask());

        Assert.NotEmpty(_logger.Entries);
        Assert.All(_logger.Entries, entry =>
        {
            Assert.DoesNotContain(_clientSecret, entry.Everything, StringComparison.Ordinal);
            Assert.DoesNotContain("issued-token", entry.Everything, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Options_DoNotPrintTheSecret()
    {
        var options = new ClientCredentialsOptions { ClientSecret = _clientSecret };

        Assert.DoesNotContain(_clientSecret, options.ToString(), StringComparison.Ordinal);
    }

    private (ClientCredentialsTokenProvider Provider, StubHttpMessageHandler Handler) Create(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<ClientCredentialsOptions>? configure = null) =>
        Create((request, _) => Task.FromResult(respond(request)), configure);

    private (ClientCredentialsTokenProvider Provider, StubHttpMessageHandler Handler) Create(
        Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond,
        Action<ClientCredentialsOptions>? configure = null)
    {
        var options = new ClientCredentialsOptions
        {
            TokenEndpoint = TokenEndpoint,
            ClientId = ClientId,
            ClientSecret = _clientSecret,
            Audience = Audience
        };
        configure?.Invoke(options);

        var handler = new StubHttpMessageHandler(respond);
        var factory = new StubHttpClientFactory(handler);
        var provider = new ClientCredentialsTokenProvider(factory, Options.Create(options), _time, _logger);
        return (provider, handler);
    }

    /// <summary>Issues token-1, token-2, … in order.</summary>
    private Func<RecordedRequest, HttpResponseMessage> IssueTokens(int? expiresIn = 3600) =>
        _ => TokenResponse($"token-{Interlocked.Increment(ref _issued)}", expiresIn);

    private static HttpResponseMessage TokenResponse(string accessToken, int? expiresIn) =>
        StubHttpMessageHandler.Json(
            HttpStatusCode.OK,
            expiresIn is null
                ? $$"""{ "access_token": "{{accessToken}}", "token_type": "Bearer" }"""
                : $$"""{ "access_token": "{{accessToken}}", "token_type": "Bearer", "expires_in": {{expiresIn}} }""");
}
