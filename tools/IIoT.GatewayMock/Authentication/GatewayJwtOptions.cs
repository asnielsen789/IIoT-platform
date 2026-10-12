namespace IIoT.GatewayMock.Authentication;

/// <summary>
/// How the mock validates the bearer token on every request. Bound from
/// <c>GatewayMock:Jwt</c>, so a real identity provider can be plugged in by configuration alone.
/// </summary>
/// <remarks>
/// Nothing here is secret. Tokens are signed with an asymmetric key: the issuer holds the private
/// half, and the gateway needs only the public half, which can verify a signature but cannot
/// create one. That is also why only RS256 is accepted — a symmetric algorithm such as HS256 would
/// mean sharing a signing secret with every receiver.
/// </remarks>
public sealed class GatewayJwtOptions
{
    public const string SectionName = "GatewayMock:Jwt";

    /// <summary>
    /// An OpenID Connect issuer whose discovery document publishes the signing keys — for Auth0,
    /// <c>https://&lt;tenant&gt;.&lt;region&gt;.auth0.com/</c>. Optional when
    /// <see cref="SigningKeys"/> is set. Must be HTTPS: the signing keys are fetched from it.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>The expected <c>iss</c> claim. Required.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// The expected <c>aud</c> claim: the identifier of the API the token must have been issued
    /// for. Required. Without it, a token issued by the same provider for any other API would be
    /// accepted here.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// PEM-encoded RSA public keys (<c>-----BEGIN PUBLIC KEY-----</c>) trusted to sign tokens,
    /// for an issuer without a discovery document. Public keys only.
    /// </summary>
    public List<string> SigningKeys { get; set; } = [];
}
