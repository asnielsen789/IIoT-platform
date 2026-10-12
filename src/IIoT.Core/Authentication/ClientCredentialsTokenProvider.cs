using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IIoT.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IIoT.Core.Authentication;

/// <summary>
/// Obtains access tokens with the OAuth 2.0 client credentials grant (RFC 6749, section 4.4),
/// caches them, and renews them before they expire.
/// </summary>
/// <remarks>
/// <para>
/// Provider-agnostic: it speaks the RFC, not a vendor SDK. Auth0 is the reference deployment,
/// and needs nothing beyond the <c>audience</c> parameter the options already carry.
/// </para>
/// <para>
/// Registered as a singleton, because the cache is the point. It therefore must not hold an
/// <see cref="HttpClient"/>: one captured by a singleton keeps its connection handler forever and
/// never sees a DNS change. It asks <see cref="IHttpClientFactory"/> for a client per request
/// instead, which is cheap — the factory pools and recycles the handlers underneath.
/// </para>
/// <para>
/// Concurrent callers share one refresh. Without that, a burst of messages arriving just as the
/// token expires would each fetch their own token, and a provider that rate-limits its token
/// endpoint (Auth0 does) would start refusing.
/// </para>
/// </remarks>
public sealed class ClientCredentialsTokenProvider : IAccessTokenProvider
{
    /// <summary>The named <see cref="HttpClient"/> used for the token endpoint.</summary>
    public const string HttpClientName = "IIoT.ClientCredentials";

    private const int MaxReportedLength = 200;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ClientCredentialsOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClientCredentialsTokenProvider> _logger;

    /// <summary>One refresh at a time; everyone else waits for its result.</summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Read and written with <see cref="Volatile"/>, so the fast path needs no lock.</summary>
    private CachedToken? _cached;

    public ClientCredentialsTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<ClientCredentialsOptions> options,
        TimeProvider timeProvider,
        ILogger<ClientCredentialsTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && IsFresh(cached))
        {
            return cached.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Whoever held the lock before us may already have refreshed it.
            cached = Volatile.Read(ref _cached);
            if (cached is not null && IsFresh(cached))
            {
                return cached.AccessToken;
            }

            var fresh = await RequestTokenAsync(cancellationToken);
            Volatile.Write(ref _cached, fresh);
            return fresh.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate(string accessToken)
    {
        var cached = Volatile.Read(ref _cached);

        // Only the token that was rejected. If another caller has already replaced it, the new
        // one has not been rejected by anyone and stays.
        if (cached is not null && string.Equals(cached.AccessToken, accessToken, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _cached, null, cached);
        }
    }

    private bool IsFresh(CachedToken token) => _timeProvider.GetUtcNow() < token.RenewAt;

    private async Task<CachedToken> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        // Checked before anything is sent: a misconfigured endpoint must not receive the secret.
        var problem = DescribeConfigurationProblem(options);
        if (problem is not null)
        {
            throw new AccessTokenException($"The client credentials grant is not configured: {problem}");
        }

        var endpoint = options.TokenEndpoint!;
        var endpointName = $"{endpoint.Scheme}://{endpoint.Authority}{endpoint.AbsolutePath}";

        // The lifetime is counted from before the request, not after the response: the token
        // started ageing when the provider issued it, which is no earlier than this.
        var requestedAt = _timeProvider.GetUtcNow();

        string body;
        HttpStatusCode status;
        try
        {
            using var request = BuildRequest(options);
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new AccessTokenException($"Token request to {endpointName} failed: {ex.Message}", ex);
        }

        if ((int)status is < 200 or > 299)
        {
            throw new AccessTokenException(
                $"Token endpoint {endpointName} responded {(int)status} {status}{DescribeOAuthError(body)}");
        }

        var token = ParseToken(body, endpointName);

        var renewAt = requestedAt + token.Lifetime - RenewalMargin(token.Lifetime, options.RenewalMargin);

        if (token.Lifetime == TimeSpan.Zero)
        {
            _logger.LogWarning(
                "Token endpoint {TokenEndpoint} returned a token without a usable expires_in; it will not be cached",
                endpointName);
        }
        else
        {
            _logger.LogInformation(
                "Obtained an access token from {TokenEndpoint}, valid for {Lifetime}; renewing after {RenewIn}",
                endpointName,
                token.Lifetime,
                renewAt - requestedAt);
        }

        return new CachedToken(token.AccessToken, renewAt);
    }

    private static HttpRequestMessage BuildRequest(ClientCredentialsOptions options)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };

        var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (options.ClientAuthentication == ClientAuthenticationMethod.ClientSecretBasic)
        {
            // RFC 6749 section 2.3.1: each part is form-urlencoded before the two are joined,
            // so a colon inside a secret cannot be mistaken for the separator.
            var credentials = $"{WebUtility.UrlEncode(options.ClientId)}:{WebUtility.UrlEncode(options.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
        else
        {
            form.Add(new("client_id", options.ClientId!));
            form.Add(new("client_secret", options.ClientSecret!));
        }

        if (!string.IsNullOrWhiteSpace(options.Audience))
        {
            form.Add(new("audience", options.Audience));
        }

        if (!string.IsNullOrWhiteSpace(options.Scope))
        {
            form.Add(new("scope", options.Scope));
        }

        request.Content = new FormUrlEncodedContent(form);
        return request;
    }

    private static string? DescribeConfigurationProblem(ClientCredentialsOptions options)
    {
        var endpointProblem = TransportSecurity.DescribeViolation(options.TokenEndpoint, nameof(options.TokenEndpoint));
        if (endpointProblem is not null)
        {
            return endpointProblem;
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return $"{nameof(options.ClientId)} is not configured";
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return $"{nameof(options.ClientSecret)} is not configured";
        }

        return options.RenewalMargin < TimeSpan.Zero
            ? $"{nameof(options.RenewalMargin)} must not be negative"
            : null;
    }

    /// <summary>The configured margin, but never more than half the token's lifetime.</summary>
    private static TimeSpan RenewalMargin(TimeSpan lifetime, TimeSpan configured) =>
        configured < lifetime / 2 ? configured : lifetime / 2;

    /// <summary>
    /// RFC 6749 section 5.1. <c>access_token</c> and <c>token_type</c> are required;
    /// <c>expires_in</c> is only recommended, and without it the token is used once and not
    /// cached, since there is no safe way to tell how long it lives.
    /// </summary>
    private static IssuedToken ParseToken(string body, string endpointName)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(accessToken.GetString()))
            {
                throw new AccessTokenException($"Token endpoint {endpointName} answered without an access_token");
            }

            // RFC 6749 section 7.1: the type is case-insensitive. Anything but a bearer token
            // would need a different way of presenting it, which the forwarder does not have.
            if (!root.TryGetProperty("token_type", out var tokenType) ||
                tokenType.ValueKind != JsonValueKind.String ||
                !string.Equals(tokenType.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw new AccessTokenException($"Token endpoint {endpointName} did not issue a Bearer token");
            }

            var lifetime = root.TryGetProperty("expires_in", out var expiresIn) &&
                           expiresIn.ValueKind == JsonValueKind.Number &&
                           expiresIn.TryGetInt64(out var seconds) &&
                           seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;

            return new IssuedToken(accessToken.GetString()!, lifetime);
        }
        catch (JsonException)
        {
            throw new AccessTokenException($"Token endpoint {endpointName} answered with something that is not JSON");
        }
    }

    /// <summary>
    /// RFC 6749 section 5.2. Only the standard <c>error</c> and <c>error_description</c> fields are
    /// reported, never the raw body, which is not guaranteed to be free of what was sent.
    /// </summary>
    private static string DescribeOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

            if (string.IsNullOrEmpty(error))
            {
                return "";
            }

            return string.IsNullOrEmpty(description)
                ? $": {Truncate(error)}"
                : $": {Truncate(error)} ({Truncate(description)})";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxReportedLength ? value : value[..MaxReportedLength] + "...";

    private sealed record IssuedToken(string AccessToken, TimeSpan Lifetime)
    {
        // Never print the token, even by accident.
        public override string ToString() => $"IssuedToken {{ Lifetime = {Lifetime} }}";
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset RenewAt)
    {
        public override string ToString() => $"CachedToken {{ RenewAt = {RenewAt:O} }}";
    }
}
