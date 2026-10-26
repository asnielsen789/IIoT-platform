# IIoT.GatewayMock — downstream gateway stand-in

A minimal ASP.NET Core API that stands in for the downstream gateway. It implements the receiving
side of the [gateway contract](../../docs/gateway-contract.md) — `POST /v1/readings` — and
nothing else. A development tool: it is never deployed as part of the platform.

> **No secrets belong in its configuration.** It verifies tokens with *public* keys and an
> issuer URL, neither of which is secret, and it refuses to start if handed a private key.

---

## What it checks, on every request

1. **The bearer token** — signature (RS256 only), issuer, audience and lifetime. A missing or
   invalid token is `401`, before anything looks at the body.
2. **`Content-Type: application/json`** — otherwise `415`.
3. **`Idempotency-Key`** — present, and equal to the body's `messageId` — otherwise `400`.
4. **The body against the schema** — [`readings-v1.schema.json`](../../docs/schemas/gateway/readings-v1.schema.json),
   linked into the assembly from `docs/` so it validates against the published bytes. A failure is
   `400` with the reason as RFC 9457 problem details.

A batch that passes is stored in memory and answered `202 Accepted`. A replay — same device, same
key — is answered `202` too, and nothing is stored twice.

The mock deliberately does **not** reference `IIoT.Core`. If it judged requests with the
platform's own types, the forwarder and the mock could agree with each other while both
disagreed with the published schema. The schema is the single arbiter.

Every endpoint requires an authenticated caller: an authorization fallback policy covers any
endpoint added later, so protection does not depend on someone remembering to add it. There is
no inspection endpoint; tests read the in-memory store directly, in-process.

---

## Configuration

Section `GatewayMock:Jwt`:

| Key | Required | Meaning |
|-----|----------|---------|
| `Issuer` | Yes | The expected `iss` claim. |
| `Audience` | Yes | The expected `aud` claim — the gateway API's identifier. |
| `Authority` | One of these two | An OpenID Connect issuer URL (HTTPS). The signing keys are fetched from its discovery document and refreshed when the provider rotates them. |
| `SigningKeys` | One of these two | PEM-encoded RSA **public** keys, for an issuer without a discovery document. |

The mock does not start with either `Issuer` or `Audience` missing, with neither `Authority` nor
`SigningKeys`, with a plain-HTTP `Authority`, or with a private key in `SigningKeys`. The
framework's default would be to silently stop checking an issuer or audience that is not
configured; here a gap in the configuration is a refusal to start.

### Against Auth0

With the tenant and API set up as in
[Service-to-service authentication (one-time)](../../infra/README.md#service-to-service-authentication-one-time)
([#12](https://github.com/asnielsen789/IIoT-platform/issues/12)), which also shows how to send it a
token from the tenant:

```
dotnet run --project tools/IIoT.GatewayMock -- \
  --GatewayMock:Jwt:Authority=https://<tenant>.<region>.auth0.com/ \
  --GatewayMock:Jwt:Issuer=https://<tenant>.<region>.auth0.com/ \
  --GatewayMock:Jwt:Audience=<the API identifier>
```

Environment variables work as well, with `__` for `:` (`GatewayMock__Jwt__Audience`).

The launch profile listens on `https://localhost:7180` only, using the ASP.NET Core development
certificate (`dotnet dev-certs https --trust`). There is no plain-HTTP listener: a token sent
over HTTP has already crossed the wire in clear text before any redirect could help.

### Without an identity provider

There is no command-line tool here for minting tokens. The integration tests in
`tests/IIoT.Integration.Tests` generate an RSA key pair in memory at runtime, configure the mock
with the public half, and sign their own tokens with the private half — nothing is written to
disk. Running the mock by hand needs a real issuer, which is what #12 provides.

---

## What is tested

`tests/IIoT.Integration.Tests` hosts the mock in-process with `WebApplicationFactory` — no
socket is opened — and covers:

- every `valid-` gateway example is accepted, every `invalid-` one rejected with a reason;
- no token, and tokens that are expired, not yet valid, for another audience, from another
  issuer, signed by an untrusted key, unsigned, or HS256-signed with the public key — all `401`;
- replays acknowledged without a duplicate, keys scoped per device;
- configuration that would weaken validation stops the mock from starting;
- the platform's real forwarder and token provider delivering to it, end to end.
