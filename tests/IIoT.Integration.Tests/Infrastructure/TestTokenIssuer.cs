using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// A stand-in for the identity provider: it signs JWTs with an RSA key generated when it is
/// created, and hands the mock only the public half.
/// </summary>
/// <remarks>
/// The key exists only in memory for the life of the test run. Nothing is read from or written to
/// a file, so there is no test key in the repository to be mistaken for a real one, or to leak.
/// </remarks>
public sealed class TestTokenIssuer : IDisposable
{
    public const string Issuer = "https://token-issuer.iiot.test/";
    public const string Audience = "https://gateway.iiot.test";
    public const string ClientId = "iiot-platform-test";

    /// <summary>The subject an M2M token carries: <c>{client id}@clients</c>, as Auth0 issues it.</summary>
    public const string Subject = ClientId + "@clients";

    private readonly RSA _rsa = RSA.Create(2048);

    public TestTokenIssuer()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>What the gateway is configured to trust. A public key, so not a secret.</summary>
    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    /// <summary>The configuration the mock needs to trust this issuer.</summary>
    public Dictionary<string, string?> GatewaySettings() => new()
    {
        ["GatewayMock:Jwt:Authority"] = "",
        ["GatewayMock:Jwt:Issuer"] = Issuer,
        ["GatewayMock:Jwt:Audience"] = Audience,
        ["GatewayMock:Jwt:SigningKeys:0"] = PublicKeyPem
    };

    /// <summary>
    /// A token that the mock accepts unless one of the arguments says otherwise.
    /// </summary>
    public string CreateToken(
        string audience = Audience,
        string issuer = Issuer,
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        SigningCredentials? signingCredentials = null,
        bool unsigned = false)
    {
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = Subject },
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued + (lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = unsigned
                ? null
                : signingCredentials ?? new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public void Dispose() => _rsa.Dispose();
}
