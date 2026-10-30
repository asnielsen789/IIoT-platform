# IIoT.GatewayMock — downstream gateway stand-in

A minimal ASP.NET Core API that stands in for the downstream gateway. It implements the receiving
side of the [gateway contract](../../docs/gateway-contract.md), in both its versions:
`POST /v1/readings` and `POST /v2/readings`
([ADR-011](../../docs/adr/adr-011-multi-type-telemetry.md)), and nothing else. A development tool: it is never deployed as part of the platform.

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
4. **The body against the path's schema** —
   [`readings-v1.schema.json`](../../docs/schemas/gateway/readings-v1.schema.json) on `/v1`,
   [`readings-v2.schema.json`](../../docs/schemas/gateway/readings-v2.schema.json) on `/v2`, each
   linked into the assembly from `docs/` so it validates against the published bytes. Each
   version takes only its own bodies: `contractVersion` must match the path. A failure is `400`
   with the reason as RFC 9457 problem details. v2's per-kind rules ask every reading whether it
   is each kind, and a "no" is not reported, so the reason names only the rule that was broken.

A batch that passes is stored in memory and answered `202 Accepted`. A replay — same device, same
key — is answered `202` too, and nothing is stored twice. A message is one message whichever
version delivers it: delivered on `/v1` and replayed on `/v2`, it is stored once.

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

Three views, in red and white
([#77](https://github.com/asnielsen789/IIoT-platform/issues/77)):

- **The overview** (`/dashboard`): totals (devices, messages, readings, the last message) and
  one row per device: what type it looks like, the latest value of each of its series, the worst
  quality among them, when the newest was measured, when its last message arrived, its message
  count and its firmware. A row opens the device. "Latest" means latest by measurement time, not
  by arrival, so a late backfill or a redelivered message never displaces a newer reading.
- **A device** (`/dashboard/device?id=<device>`): the latest reading of each series, a chart per
  kind it measures (its newest 100 readings per series, by measurement time, a line per sensor,
  each reading's quality as the ring around its dot), and its message history, newest first (the
  newest 100): when the platform received each message, its `messageId` (which is also the
  `Idempotency-Key`), its readings, its worst quality and its
  firmware. A replay is acknowledged but not stored again, so it never appears twice.
- **A message** (`&message=<messageId>` added to the device's address): a modal over the device's
  page with the batch as the mock stored it, as indented, coloured JSON, and its metadata: when
  the platform received it, when the mock stored it, its `Idempotency-Key`, and the authenticated
  caller (the token's `sub`). Its close button and its backdrop are links back to the device.

**Series and device types** ([ADR-011](../../docs/adr/adr-011-multi-type-telemetry.md)). A
series is a reading kind and, where a device has several sensors of that kind, the sensor: an oil
separator has `Level · oil` and `Level · sludge`. A kind's sensors share one chart and one scale,
as they share the unit. For the cumulative kinds, `energy`, `volume` and `count`, the line is the
meter's running total, so its slope is the rate of use. The type a device is shown as (tank, EV
charger, car wash, oil separator) is a guess from the series it sends, for presentation only: the
contract has no device type, and readings are judged by kind. Units are shown as people write them
(`kW.h` as kWh; a count's `1` not at all).

Device and message IDs are in the query string, escaped, because either may hold any character,
`/` included. An address the mock no longer knows, from before a restart, is a 404 page with a
link to the overview. Times are in UTC; a time that came in a body is shown formatted when it
can be read, with the value as sent in a tooltip. On a narrow screen the tables become stacked
rows.

The overview and a device's page refresh themselves every 10 seconds with
`<meta http-equiv="refresh">`; an open message does not, so it stays open. Everything is rendered
on the server, by minimal-API endpoints that return Razor components, and the pages send no
script and no forms. The modal is a page of its own, and the chart and icons are inline SVG,
drawn from coordinates computed on the server. Every value on the pages came from a request body
or a token, so every value is HTML-encoded, the JSON in the message view included. The responses
also carry a `Content-Security-Policy` that forbids script, and `Cache-Control: no-store`.

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
| `/dashboard` | The overview |
| `/dashboard/device?id=…[&message=…]` | A device, and optionally one of its messages |
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

**On Linux App Service, trust `169.254.0.0/16`.** The front end reaches the app's container from
the link-local range. Link-local addresses are not routed, so nothing outside the host can
connect from them. Microsoft's sample trusts the three private ranges `10.0.0.0/8`,
`172.16.0.0/12` and `192.168.0.0/16` instead
([Configure ASP.NET Core apps: Detect HTTPS session](https://learn.microsoft.com/azure/app-service/configure-language-dotnetcore#detect-https-session)).
On staging, on 2026-10-30, those gave an `http://` redirect URI, and adding `169.254.0.0/16` made
it `https://`. The middleware matches IPv4-mapped IPv6 addresses such as `::ffff:169.254.x.y`
against IPv4 networks itself, so the plain IPv4 form is enough.

If the provider still reports an `http://` redirect URI, the address that was not trusted is
logged as `Unknown proxy: …`, at Debug level, category
`Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware`. On Linux App Service that
category cannot be raised by an app setting, because the platform turns the dots in a setting's
name into underscores. Trying a network instead, as above, settles it. `0.0.0.0/0` would trust
anyone who can reach the app.

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
`<app name>.azurewebsites.net`, is the `<mock host>` above. The templates set the dashboard up
when the parameter `gatewayMockDashboardAuthority` is set. The procedure, from the Auth0
application to the checks, is
[Gateway mock dashboard (one-time)](../../infra/README.md#gateway-mock-dashboard-one-time) in the
infrastructure README. The app then has these four settings, in addition to the three
`GatewayMock__Jwt__*` settings:

| App setting | Value |
|---|---|
| `GatewayMock__Dashboard__Authority` | `https://<tenant domain>/` |
| `GatewayMock__Dashboard__ClientId` | A Key Vault reference to the secret `gateway-mock-dashboard-client-id` |
| `GatewayMock__Dashboard__ClientSecret` | A Key Vault reference to the secret `gateway-mock-dashboard-client-secret`. **Secret** |
| `GatewayMock__ForwardedHeaders__KnownNetworks__0` | `169.254.0.0/16` |

`GatewayMock__ForwardedHeaders__Enabled` is not needed: it follows the dashboard.

The app reads the client ID and secret with its own identity, a system-assigned one with Key
Vault Secrets User on those two secrets alone. App Service resolves the references and hands the
mock the values. A reference it cannot resolve reaches the mock as the reference text, which is
why the first deployment ends with a refresh (step 3 of the procedure). The settings come from the template,
which writes all of the app's settings on every infrastructure deployment: a setting added by
hand is gone after the next one.

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

- every `valid-` gateway example of both versions is accepted, every `invalid-` one rejected with
  a reason, on its own path; each version refuses the other's bodies; a message delivered on both is
  stored once;
- no token, and tokens that are expired, not yet valid, for another audience, from another
  issuer, signed by an untrusted key, unsigned, or HS256-signed with the public key — all `401`;
- replays acknowledged without a duplicate, keys scoped per device;
- configuration that would weaken validation stops the mock from starting;
- the platform's real forwarder and token provider delivering to it, end to end;
- every API policy names the bearer scheme, and only it;
- the dashboard, disabled: its paths are `404` with a valid token, and no other scheme exists;
- the dashboard, enabled, against an in-process OpenID Connect provider that checks the client
  secret, the redirect URI and the PKCE verifier: the sign-in redirect carries a PKCE challenge;
  a signed-in browser sees the stored batches, and a device of several series its series and a
  chart per kind; markup in a device ID, message ID, firmware or user
  name is rendered as text; the session cookie alone gets `401` from the API; a bearer token alone
  does not open the dashboard; a code injected into another sign-in issues no session; the
  cookie's attributes; sign-out at both ends, with an `id_token_hint` the provider issued;
- `X-Forwarded-Proto` believed from a trusted network only, rightmost value only, and
  `X-Forwarded-Host` ignored;
- every invalid dashboard or forwarded-headers setting stops startup with a message that names
  it, and never the client secret.
