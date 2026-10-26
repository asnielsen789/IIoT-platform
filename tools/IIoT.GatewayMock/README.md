# IIoT.GatewayMock — downstream gateway stand-in

A minimal ASP.NET Core API that stands in for the downstream gateway. It implements the receiving
side of the [gateway contract](../../docs/gateway-contract.md) — `POST /v1/readings` — and
nothing else. A development tool: it is never deployed as part of the platform.

> **No secrets belong in the API's configuration.** It verifies tokens with *public* keys and an
> issuer URL, neither of which is secret, and it refuses to start if handed a private key. The
> optional [dashboard](#dashboard) adds exactly one secret, its OpenID Connect client secret,
> which comes from the host's environment and never from a file in the repository.

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
endpoint added later, so protection does not depend on someone remembering to add it. Every API
policy names the bearer scheme explicitly, so no other credential — in particular the
dashboard's session cookie — can satisfy it. Tests read the in-memory store directly,
in-process; people use the [dashboard](#dashboard), when it is enabled.

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

## Dashboard

An optional, read-only page at `/dashboard` showing what the mock has received, so that the
end-to-end flow can be shown to people rather than queried
([#47](https://github.com/asnielsen789/IIoT-platform/issues/47)). It is **off** unless
`GatewayMock:Dashboard:Authority` is set. Without it nothing of the dashboard is registered or
mapped: `/dashboard` is a path like any other, and the mock is the API alone.

### What it shows

- **The latest reading per device**: value and unit, quality, when it was measured (device
  clock), when the platform received it, and the message it came in. "Latest" means latest by
  measurement time, not by arrival, so a late backfill or a redelivered message never displaces
  a newer reading.
- **Every received batch, newest first** (the newest 100): when the mock stored it, its
  `receivedAt`, the device, the `messageId` (which is also the `Idempotency-Key`), the
  authenticated caller (the token's `sub`), the firmware and every reading. A replay is
  acknowledged but not stored again, so it never appears twice.

The page refreshes itself every 10 seconds with `<meta http-equiv="refresh">`. It is rendered on
the server, by a minimal-API endpoint that returns one Razor component, and it sends no script
and no forms. Every value on it came from a request body or a token, so every value is
HTML-encoded. The response also carries a `Content-Security-Policy` that forbids script, and
`Cache-Control: no-store`.

### How sign-in works

A browser cannot present the API's bearer token, so the dashboard has its own sign-in:
**OpenID Connect** with the authorization code flow and **PKCE**. The code is posted back in a
form (`response_mode=form_post`) and redeemed server to server with the client secret, and only
**RS256**-signed ID tokens are accepted. Sign-in ends in a session cookie. Any user the provider
signs in may look: the page is read-only, so there are no roles. Who can sign in at all is
decided at the provider (see sign-ups under [At the identity provider](#at-the-identity-provider)).

Neither side's credential works on the other:

| | API (`POST /v1/readings`) | Dashboard (`/dashboard`) |
|---|---|---|
| Accepts | a bearer JWT only | the dashboard's session cookie only |
| Without it | `401` with `WWW-Authenticate: Bearer` | a redirect to the provider's sign-in |
| With the other's credential | the cookie is ignored: `401` | the bearer token is ignored: redirect |

The cookie, `IIoT.GatewayMock.Dashboard`, has these properties:
- **Scoped to `/dashboard`**, so a browser never attaches it to an API request.
- **Always `Secure`, `HttpOnly` and `SameSite=Lax`.**
- **No expiry of its own:** it ends with the browser session.
- **A sign-in lasts at most 8 hours.** After that the next refresh goes back to the provider,
  silently while the provider's own session lasts.
- **It holds the ID token, for sign-out, and no access token.**

**Sign-out** (`/dashboard/sign-out`, the link on the page) ends the session here and at the
provider (OpenID Connect RP-Initiated Logout, with `id_token_hint` and `client_id`). The provider
then sends the browser back to the dashboard, which asks for a new sign-in.

### Settings

Section `GatewayMock:Dashboard`, or `GatewayMock__Dashboard__…` as environment variables and app
settings:

| Key | Required | Meaning |
|-----|----------|---------|
| `Authority` | Switches the dashboard on | The OpenID Connect issuer people sign in with; for Auth0, `https://<tenant domain>/`. Must be HTTPS: the sign-in page, the token endpoint and the keys that verify the ID token all come from its discovery document. Nothing else is provider-specific. |
| `ClientId` | When enabled | The dashboard application's client ID. |
| `ClientSecret` | When enabled | The dashboard application's client secret. **Secret:** an environment variable, or an app setting that is a Key Vault reference, never a committed file. It is never logged. |

When `Authority` is set, the mock refuses to start if it is not an absolute HTTPS URL, or if
`ClientId` or `ClientSecret` is missing. The message names the setting, never its value. A client
ID and secret without an `Authority` leave the dashboard off.

All of the dashboard's paths are under `/dashboard`:

| Path | What it is |
|------|------------|
| `/dashboard` | The page |
| `/dashboard/sign-out` | Sign-out |
| `/dashboard/signin-oidc` | Where the provider returns after sign-in: the **callback URL** to register |
| `/dashboard/signout-callback-oidc` | Where the provider returns after sign-out: the **logout URL** to register |

### Behind a TLS-terminating proxy

On App Service, TLS ends at the platform's front end. Every request reaches the app as plain
HTTP, with the browser's scheme in `X-Forwarded-Proto`. The sign-in builds its redirect URI from
the request's scheme, so without that header it would ask the provider to send the user back to
`http://`, and the provider refuses that ("Callback URL mismatch"). Section
`GatewayMock:ForwardedHeaders`:

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | on when the dashboard is on | `true` or `false` to decide explicitly. The API does not need it. |
| `KnownNetworks` | none: loopback only | Networks, in CIDR notation, whose `X-Forwarded-Proto` is believed, in addition to loopback. A single proxy is a `/32`. A bare address, or anything else that is not CIDR, stops startup. |

From any other address the header is ignored, so a client that reaches the app directly cannot
choose its scheme. These limits apply:
- **Only the rightmost value counts:** the one written by the proxy next to the app.
- **Only the scheme is taken.** `X-Forwarded-Host` is **not** honoured: a forged one would choose
  the host the authorization code is sent to. `X-Forwarded-For` is not used.
- **The host comes from `Host`.** The redirect URI's host is the request's `Host` header, which
  App Service passes through; the check in [Switching it on in staging](#switching-it-on-in-staging)
  shows it.

For App Service, Microsoft's guidance is to trust the three private ranges its front end connects
from ([Configure ASP.NET Core apps: Detect HTTPS session](https://learn.microsoft.com/azure/app-service/configure-language-dotnetcore#detect-https-session)):
`10.0.0.0/8`, `172.16.0.0/12` and `192.168.0.0/16`. A request from the internet does not arrive
from those addresses. If the provider still reports an `http://` redirect URI, the
forwarded-headers middleware logs the address it did not trust (`Unknown proxy: …`, at Debug
level, category `Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware`); trust that
network. `0.0.0.0/0` would trust anyone who can reach the app.

Leave `ASPNETCORE_FORWARDEDHEADERS_ENABLED` unset. That framework switch believes
`X-Forwarded-For` and `X-Forwarded-Proto` from **any** address, before these rules apply.

### At the identity provider

In Auth0, in the same tenant as the Function's machine-to-machine application
([Service-to-service authentication](../../infra/README.md#service-to-service-authentication-one-time)),
but as a separate application. Never reuse the machine-to-machine client.

- **Applications → Create Application → Regular Web Application.**
- **Allowed Callback URLs:** `https://<mock host>/dashboard/signin-oidc`, and for local runs
  `https://localhost:7180/dashboard/signin-oidc`.
- **Allowed Logout URLs:** `https://<mock host>/dashboard/signout-callback-oidc`, and for local
  runs `https://localhost:7180/dashboard/signout-callback-oidc`.
- **Allowed Web Origins** and **Allowed Origins (CORS):** empty, since the page runs no script.
- **Advanced Settings → Grant Types:** **Authorization Code** only. Clear Implicit, Refresh Token
  and Client Credentials.
- **Credentials → Authentication Method:** **Client Secret (Post)**, which is what the sign-in
  sends (`client_secret_post`).
- **Advanced Settings → OAuth:** JSON Web Token Signature Algorithm **RS256**, and OIDC Conformant
  on. Both are the defaults for a new application.
- **Who can sign in:** on the application's **Connections** tab, enable only the connection its
  users are in, such as the database connection. On that connection, turn on **Disable Sign
  Ups**: otherwise anyone can create an account on the sign-in page and look at the dashboard.
  Create the viewers under **User Management → Users**.
- **For sign-out:** turn on tenant **Settings → Advanced → Login and Logout → RP-Initiated Logout
  End Session Endpoint Discovery**, so that the discovery document lists `end_session_endpoint`.
  It is on by default for tenants created on or after 14 November 2023. Without it, sign-out
  fails.

The application's **Domain**, **Client ID** and **Client Secret** become `Authority`
(`https://<domain>/`), `ClientId` and `ClientSecret`.

### Running it locally

Use the API's settings from [Against Auth0](#against-auth0), the dashboard's client ID on the
command line, and the secret in an environment variable. `read -s` reads it without echo, so it
stays out of the terminal, the shell history and the process list:

```sh
read -rs -p "Dashboard client secret: " GatewayMock__Dashboard__ClientSecret && echo
export GatewayMock__Dashboard__ClientSecret

dotnet run --project tools/IIoT.GatewayMock -- \
  --GatewayMock:Jwt:Authority=https://<tenant domain>/ \
  --GatewayMock:Jwt:Issuer=https://<tenant domain>/ \
  --GatewayMock:Jwt:Audience=<the API identifier> \
  --GatewayMock:Dashboard:Authority=https://<tenant domain>/ \
  --GatewayMock:Dashboard:ClientId=<the dashboard application's client ID>

unset GatewayMock__Dashboard__ClientSecret
```

The log says `Dashboard enabled at /dashboard`. Open `https://localhost:7180/dashboard` and sign
in. The launch profile serves HTTPS itself, so no forwarded header is involved.

### Switching it on in staging

The hosted mock is the App Service app `app-iiot-staging-sdc-gateway-mock-<suffix>`
([infra/README.md, Gateway mock](../../infra/README.md#gateway-mock)). Its host,
`<app name>.azurewebsites.net`, is the `<mock host>` above. Once the Auth0 application is set up
for that host, the app needs these six settings, in addition to the three `GatewayMock__Jwt__*`
settings the templates already set:

| App setting | Value |
|---|---|
| `GatewayMock__Dashboard__Authority` | `https://<tenant domain>/` |
| `GatewayMock__Dashboard__ClientId` | The dashboard application's client ID |
| `GatewayMock__Dashboard__ClientSecret` | The dashboard application's client secret. **Secret** |
| `GatewayMock__ForwardedHeaders__KnownNetworks__0` | `10.0.0.0/8` |
| `GatewayMock__ForwardedHeaders__KnownNetworks__1` | `172.16.0.0/12` |
| `GatewayMock__ForwardedHeaders__KnownNetworks__2` | `192.168.0.0/16` |

`GatewayMock__ForwardedHeaders__Enabled` is not needed: it follows the dashboard.

Two properties of the current template, `infra/modules/gateway-mock.bicep`, decide how the
settings get there:
- **The template owns the app settings.** Every infrastructure deployment writes the app's
  settings from the template, so a setting added outside it is gone afterwards. The lasting place
  for all six is the template.
- **The app has no managed identity yet,** so it cannot resolve a Key Vault reference. The secret
  belongs in Key Vault, referenced like the Function's `GatewayAuth__ClientSecret`
  (`@Microsoft.KeyVault(SecretUri=…)`). That needs the app to get an identity with Key Vault
  Secrets User on that secret. Until the template does that, the secret can only be a plain app
  setting: App Service encrypts it at rest, but anyone who can read the app's settings can read
  it.

To try the dashboard before the template carries it, set the six by hand, and again after every
infrastructure deployment. The secret is read without echo:

```sh
read -rs -p "Dashboard client secret: " DASHBOARD_SECRET && echo
az webapp config appsettings set --resource-group "$RG" --name "$MOCK_APP" -o none --settings \
  GatewayMock__Dashboard__Authority=https://<tenant domain>/ \
  GatewayMock__Dashboard__ClientId=<the dashboard application's client ID> \
  GatewayMock__Dashboard__ClientSecret="$DASHBOARD_SECRET" \
  GatewayMock__ForwardedHeaders__KnownNetworks__0=10.0.0.0/8 \
  GatewayMock__ForwardedHeaders__KnownNetworks__1=172.16.0.0/12 \
  GatewayMock__ForwardedHeaders__KnownNetworks__2=192.168.0.0/16
unset DASHBOARD_SECRET
```

The app restarts with the new settings. It worked when all four hold:
- `az webapp log tail` shows `Dashboard enabled at /dashboard`. If the mock does not start
  instead, the log names the setting that is wrong.
- `https://<mock host>/dashboard` redirects to the tenant's sign-in page.
- That page's address contains `redirect_uri=https%3A%2F%2F<mock host>%2Fdashboard%2Fsignin-oidc`:
  HTTPS, and the app's own host.
- `POST /v1/readings` without a token is still `401`.

The session cookie and the sign-in state are protected with ASP.NET Core Data Protection keys. On
App Service the framework keeps those keys in the app's `%HOME%` storage
([Data Protection key management](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/default-settings)),
so a restart keeps people signed in. It still empties the readings.

### Not persisted

The readings stay in memory: a restart, or the app unloading when idle (App Service F1 has no
Always On), empties them. There is no SQLite or other persistence, for three reasons:
- **It would not stay small.** It adds a native dependency to the mock and its SBOM, a file
  location to configure, and a second store behind `ReadingStore` for the tests to keep in step.
- **The host is a poor fit.** On App Service the only persistent location is the network-backed
  `/home` share, where SQLite's file locking is not dependable.
- **A demonstration does not need it.** What it needs is the next message, which arrives within
  minutes; the in-memory store is also what the tests use.

---

## What is tested

`tests/IIoT.Integration.Tests` hosts the mock in-process with `WebApplicationFactory` — no
socket is opened — and covers:

- every `valid-` gateway example is accepted, every `invalid-` one rejected with a reason;
- no token, and tokens that are expired, not yet valid, for another audience, from another
  issuer, signed by an untrusted key, unsigned, or HS256-signed with the public key — all `401`;
- replays acknowledged without a duplicate, keys scoped per device;
- configuration that would weaken validation stops the mock from starting;
- the platform's real forwarder and token provider delivering to it, end to end;
- every API policy names the bearer scheme, and only it;
- the dashboard, disabled: its paths are `404` with a valid token, and no other scheme exists;
- the dashboard, enabled, against an in-process OpenID Connect provider that checks the client
  secret, the redirect URI and the PKCE verifier: the sign-in redirect carries a PKCE challenge;
  a signed-in browser sees the stored batches; markup in a device ID, message ID, firmware or user
  name is rendered as text; the session cookie alone gets `401` from the API; a bearer token alone
  does not open the dashboard; a code injected into another sign-in issues no session; the
  cookie's attributes; sign-out at both ends, with an `id_token_hint` the provider issued;
- `X-Forwarded-Proto` believed from a trusted network only, rightmost value only, and
  `X-Forwarded-Host` ignored;
- every invalid dashboard or forwarded-headers setting stops startup with a message that names
  it, and never the client secret.
