namespace IIoT.Core.Authentication;

/// <summary>
/// Settings for the OAuth 2.0 client credentials grant (RFC 6749, section 4.4). Nothing here is
/// specific to one identity provider.
/// </summary>
/// <remarks>
/// A plain class rather than a record on purpose: a record's generated <c>ToString</c> prints
/// every property, and one of these is a secret.
/// </remarks>
public sealed class ClientCredentialsOptions
{
    /// <summary>
    /// The token endpoint — for Auth0, <c>https://&lt;tenant&gt;.&lt;region&gt;.auth0.com/oauth/token</c>.
    /// Must be HTTPS: the client secret is sent to it.
    /// </summary>
    public Uri? TokenEndpoint { get; set; }

    public string? ClientId { get; set; }

    /// <summary>
    /// The client secret. Supplied by configuration only — in Azure, a Key Vault reference in an
    /// app setting; locally, <c>local.settings.json</c>, which is gitignored. Never committed,
    /// and never written to a log or an exception message.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The API the token is for, sent as <c>audience</c>. Auth0 requires it; providers that
    /// identify the API through <see cref="Scope"/> instead leave it unset, and it is not sent.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>Sent as <c>scope</c> when set.</summary>
    public string? Scope { get; set; }

    /// <summary>How the client authenticates to the token endpoint.</summary>
    public ClientAuthenticationMethod ClientAuthentication { get; set; } = ClientAuthenticationMethod.ClientSecretPost;

    /// <summary>
    /// How long before expiry a cached token is replaced. The margin keeps a token from expiring
    /// between being handed out and being checked by the receiver, including across retries and
    /// a modest difference between the two clocks. Capped at half the token's lifetime, so a
    /// short-lived token is still reused rather than fetched for every call.
    /// </summary>
    public TimeSpan RenewalMargin { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Client authentication at the token endpoint, named as in the OAuth registry.</summary>
public enum ClientAuthenticationMethod
{
    /// <summary>
    /// <c>client_secret_post</c>: client id and secret in the form body. What Auth0 configures
    /// for a machine-to-machine application by default.
    /// </summary>
    ClientSecretPost,

    /// <summary>
    /// <c>client_secret_basic</c>: HTTP Basic authentication — the method RFC 6749 section 2.3.1
    /// requires every authorization server to support.
    /// </summary>
    ClientSecretBasic
}
