using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IIoT.GatewayMock.Authentication;

/// <summary>
/// Turns <see cref="GatewayJwtOptions"/> into the bearer handler's validation rules.
/// </summary>
/// <remarks>
/// Every rule is set explicitly rather than left to defaults. The framework's own binding from
/// <c>Authentication:Schemes:Bearer</c> switches issuer and audience validation off when no
/// issuer or audience is configured, which would turn a missing setting into a gateway that
/// accepts tokens minted for any API. Here a missing setting stops the mock from starting.
/// </remarks>
internal sealed class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    /// <summary>
    /// Tokens are minted by a server with a synchronised clock, not by a drifting device, so the
    /// framework's five-minute default tolerance is more than an expired token deserves.
    /// </summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly IOptions<GatewayJwtOptions> _jwtOptions;

    public ConfigureJwtBearerOptions(IOptions<GatewayJwtOptions> jwtOptions)
    {
        _jwtOptions = jwtOptions;
    }

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        var jwt = _jwtOptions.Value;

        // With an authority the handler fetches the signing keys from its discovery document,
        // and refreshes them when the provider rotates keys. RequireHttpsMetadata stays at its
        // default of true: keys fetched over plain HTTP could be swapped in transit.
        options.Authority = string.IsNullOrWhiteSpace(jwt.Authority) ? null : jwt.Authority;

        // Keep claim names as they appear in the token (sub, aud) instead of mapping them to
        // long WS-Federation URIs.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,

            ValidateAudience = true,
            ValidAudience = jwt.Audience,

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = ClockSkew,

            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = jwt.SigningKeys.Select(ToSigningKey).ToList()
        };
    }

    private static RsaSecurityKey ToSigningKey(string pem)
    {
        // Not disposed: the key lives as long as the options, which is the life of the process.
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa);
    }
}

/// <summary>
/// Refuses to start the mock with a configuration that cannot authenticate anything, or that
/// would authenticate too much.
/// </summary>
internal sealed class GatewayJwtOptionsValidator : IValidateOptions<GatewayJwtOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayJwtOptions options)
    {
        const string Section = GatewayJwtOptions.SectionName;
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add($"{Section}:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add($"{Section}:Audience is required.");
        }

        var hasAuthority = !string.IsNullOrWhiteSpace(options.Authority);
        if (!hasAuthority && options.SigningKeys.Count == 0)
        {
            failures.Add(
                $"Set {Section}:Authority or {Section}:SigningKeys; without one there is no key to verify a signature with.");
        }

        if (hasAuthority &&
            (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
             authority.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{Section}:Authority must be an absolute HTTPS URL; signing keys are fetched from it.");
        }

        for (var i = 0; i < options.SigningKeys.Count; i++)
        {
            var problem = DescribeSigningKeyProblem(options.SigningKeys[i]);
            if (problem is not null)
            {
                failures.Add($"{Section}:SigningKeys:{i} {problem}");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Only public keys are accepted. A private key would import just as happily and verify
    /// signatures just as well, but it is a secret, and secrets do not belong in configuration.
    /// </summary>
    private static string? DescribeSigningKeyProblem(string pem)
    {
        if (!PemEncoding.TryFind(pem, out var fields))
        {
            return "is not PEM-encoded.";
        }

        var label = pem.AsSpan()[fields.Label];
        if (!label.SequenceEqual("PUBLIC KEY") && !label.SequenceEqual("RSA PUBLIC KEY"))
        {
            return $"is a '{label}', not a public key. Configure only the public half of the signing key.";
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return "is not a valid RSA public key.";
        }
    }
}
