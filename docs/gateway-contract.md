# Gateway contract (v1)

> Normative schema: [`schemas/gateway/readings-v1.schema.json`](schemas/gateway/readings-v1.schema.json)
> Examples: [`schemas/gateway/examples/`](schemas/gateway/examples/)
> Reference receiver: [`tools/IIoT.GatewayMock`](../tools/IIoT.GatewayMock/README.md)

This document is the contract between the **IIoT platform** and a **downstream gateway**: the
format in which the platform delivers validated readings. No external gateway specification
exists ([ADR-005](adr/adr-005-scope-generalisation.md)), so the platform defines the format
itself, and the gateway mock is built against it.

---

## 1. Why a contract of its own

The device contract ([`mqtt-payload.md`](mqtt-payload.md)) and this one are versioned
**independently**, and that independence is the point of the adapter between them.

Devices stay in the field for ten years or more and update over the air at their own pace.
Downstream systems change on their owners' schedules. If the platform passed the device payload
straight through, every change to the device contract would land on every downstream system at
once. Instead, `GatewayV1Forwarder` maps one contract onto the other: a change to the device
contract changes what the platform *accepts*, and reaches a downstream system only when the
forwarder is deliberately changed to send it.

---

## 2. Request

```
POST /v1/readings
Content-Type: application/json
Authorization: Bearer <JWT>
Idempotency-Key: <messageId>
```

| Header | Required | Meaning |
|--------|----------|---------|
| `Content-Type` | Yes | `application/json`. Anything else is `415`. |
| `Authorization` | Yes | A bearer JWT. See section 6. |
| `Idempotency-Key` | Yes | The batch's `messageId`, exactly. See section 5. |

**HTTPS only.** The platform refuses to send to an address that is not `https://`, so neither the
token nor the readings ever cross a network in plaintext. The one exception is a loopback
address (`localhost`, `127.0.0.1`, `[::1]`), which never leaves the machine and which the
in-process tests use.

---

## 3. Body

```json
{
  "contractVersion": 1,
  "deviceId": "tank-000123",
  "messageId": "01J8F2K9QS4T7V0X3Z6B8N1M5B",
  "receivedAt": "2026-09-14T06:00:13.25+00:00",
  "firmware": "1.4.2",
  "readings": [
    { "kind": "level", "value": 1432.5, "unit": "mm", "at": "2026-09-14T06:00:10+00:00", "quality": "ok" }
  ]
}
```

### Batch

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `contractVersion` | integer | Yes | Always `1` under `/v1`. |
| `deviceId` | string (1–128) | Yes | The identity the platform **authenticated** (IoT Hub's `iothub-connection-device-id`). Never the `deviceId` a device wrote into its own payload, which is optional and untrusted — see section 4 of the device contract. |
| `messageId` | string (1–64) | Yes | The device's message id. Equal to the `Idempotency-Key` header. |
| `receivedAt` | ISO 8601, UTC | Yes | When the platform received the message: IoT Hub's enqueued time. Platform clock, so trustworthy as absolute time. |
| `firmware` | string (1–32) | Yes | The firmware the device reported running when it sent the message. |
| `readings` | array (1–64) | Yes | Every reading in the device's message, in the order the device sent them. |

### `readings[]`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `kind` | `"level"` | Yes | What was measured. Only `level` in v1. |
| `value` | number | Yes | The **raw** measured value, in `unit`. The platform converts nothing (ADR-004). |
| `unit` | `"mm"` | Yes | UCUM code. |
| `at` | ISO 8601 | Yes | Time of measurement by the **device** clock, offset as the device sent it. |
| `quality` | `ok` / `uncertain` / `bad` | Yes | The device's own assessment. Always present: where the device omitted it, the platform applies the device contract's default, `ok`, so a receiver never has to know that default. |

**Buffered backfill arrives as one request.** A device that buffered readings through an outage
sends them in one message, and the platform delivers that message as one batch — never one call
per reading (ADR-004). That they arrived together is how a receiver tells backfill from live
data. A reading must **never** be rejected because `at` lies far in the past; that is the normal
shape of recovered data.

### What is deliberately not forwarded

| Device field | Why it stays in the platform |
|--------------|------------------------------|
| `device.battery`, `device.signal` | Fleet operations data for the platform, not business data for a gateway. CRA Annex I Part I point (g) limits processing to what is necessary for the purpose; passing it on would widen who holds it for no stated need. |
| `sentAt` | The device clock's transmission time. The platform uses it to measure clock drift; `receivedAt` is the trustworthy time. |
| `sequence` | The platform's own loss detection. |
| `schemaVersion` | The device contract's version. This contract has its own, which is the point of section 1. |

Any of these can be added later as an optional field — an additive change, section 7 — once a
receiver has a stated need for it.

---

## 4. Responses

Error bodies are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details
(`application/problem+json`), with the reason in `detail`.

| Status | Meaning |
|--------|---------|
| `202 Accepted` | Stored — or a replay of a batch already stored, which is acknowledged the same way (section 5). |
| `400 Bad Request` | The body does not satisfy the schema, the JSON is malformed, or the `Idempotency-Key` header is missing or does not equal `messageId`. |
| `401 Unauthorized` | The token is missing or invalid. Checked before anything else, so an unauthenticated caller learns nothing about the contract. The sender retries once, with a newly obtained token (section 6). |
| `415 Unsupported Media Type` | The body is not `application/json`. |
| `408`, `429`, `5xx` | Transient. The sender retries with backoff. |

Any other `4xx` is permanent: the same request will fail the same way, so the sender does not
retry it. `408`, `429` and `5xx` are retried with backoff. `401` is the one exception among the
`4xx`: it refuses the token rather than the request, so the sender retries it once, with a new
token. `403` is not retried — the token was accepted, and the caller is not allowed.

---

## 5. Idempotency

Delivery is **at least once**. The platform reads an event stream that redelivers from its last
checkpoint after a restart, it retries a request whose response it never saw, it sends a request
refused with `401` once more with a new token, and a device may resend a message itself. A receiver will therefore see the same batch more than once, and must
treat that as normal.

- The `Idempotency-Key` is the batch's `messageId`, and must equal it — otherwise `400`. If they
  could differ, a replay check on the key would say nothing about which message was stored.
- **The key is scoped to `deviceId`.** The device contract promises that a `messageId` is unique
  per message from one device, not across the fleet. A receiver keyed on the id alone would drop
  one device's data as a "duplicate" of another's — and a device could suppress another
  device's readings by reusing its ids. Scoped to the authenticated `deviceId`, a device can only
  ever collide with itself.
- **The first delivery wins.** A replay is acknowledged with the same `202` and nothing is stored
  a second time — even when its `receivedAt` differs, which is what a device resend looks like.
- Because `POST` is made safe to repeat by the key, the sender may retry it like any idempotent
  request.

A receiver keeps keys for at least as long as the platform's event stream retains messages, since
a redelivery can arrive that late.

---

## 6. Authentication

Every request carries a JWT obtained with the OAuth 2.0 client credentials grant
([RFC 6749, section 4.4](https://www.rfc-editor.org/rfc/rfc6749#section-4.4)). The platform is the
client; the identity provider is Auth0 in the reference deployment, but nothing in the contract
depends on which provider issues the token.

A receiver validates, on **every** request:

| Check | Rule |
|-------|------|
| Signature | `RS256` only. The provider signs with a private key; the receiver holds only the public key, which can verify a signature but not create one. A symmetric algorithm would mean sharing a signing secret with every receiver. |
| Issuer | `iss` equals the configured issuer. |
| Audience | `aud` contains the gateway's API identifier. Without this, a token the same provider issued for any other API would be accepted. |
| Lifetime | `exp` is required and must not have passed; `nbf`, when present, must have. |

There are no sessions and no anonymous endpoints.

**Signing-key rotation.** When the provider rotates its signing key, a sender may still hold a
token signed with the old key that has not yet expired. A receiver that no longer trusts that key
answers `401`. The sender then discards the token, obtains a new one and sends the same request
again — same body, same `Idempotency-Key` — once. A `401` to the new token as well means the
receiver does not trust what the provider issues for this client, and the sender gives up on that
request rather than asking for token after token.

---

## 7. Evolution rules

**Additive changes** (stay on `/v1`):

- New optional fields, on the batch or on a reading.

**Breaking changes** (require `/v2`, served alongside `/v1` while receivers migrate):

- Removing or renaming a field, or making an optional field required
- Changing a field's type, unit or meaning
- Tightening a constraint so that a previously valid body is rejected
- **Adding a reading `kind`**

The last one is deliberately stricter than the device contract, where a new measurement type is
additive. There, the platform is the only consumer and is always updated before any device sends
the new type. Here the platform is the *producer*, and it cannot assume that every receiver has
been updated before it starts sending — a receiver that validates against v1 would refuse the
whole batch. A new kind therefore waits for a new version that receivers opt into.

**Receiver obligations:**

1. Unknown fields **must** be ignored, not rejected. `valid-unknown-fields.json` covers this.
2. Any `contractVersion` other than `1` on `/v1` is rejected.

`contractVersion` stays in the body even though the path carries the version, for the reason
`schemaVersion` is in the device payload (ADR-004): a stored or dead-lettered body must remain
interpretable without the URL it was posted to.

---

## 8. Examples

| File | Covers |
|------|--------|
| `valid-single-reading.json` | One reading — the normal case |
| `valid-buffered-backfill.json` | Three readings after an outage, in one request |
| `valid-unknown-fields.json` | Forward compatibility — unknown fields accepted |
| `invalid-missing-device-id.json` | No authenticated identity |
| `invalid-unknown-contract-version.json` | `contractVersion: 2` on `/v1` |
| `invalid-empty-readings.json` | Empty `readings` array |
| `invalid-missing-quality.json` | A reading without `quality` |
| `invalid-malformed-received-at.json` | `receivedAt` is not a timestamp |

Both sides are tested against these and against the schema itself: the gateway mock must accept
every `valid-` example and reject every `invalid-` one with a reason, and every body the forwarder
produces from the device contract's examples must validate against the same schema.

---

## 9. The platform's side

The sender is `GatewayV1Forwarder` in `src/IIoT.Core/Forwarders/`, a typed HTTP client registered
by `AddTelemetryForwarding` in `src/IIoT.Functions/`.

| Concern | Behaviour |
|---------|-----------|
| Batching | One request per device message, every reading included (ADR-004). |
| Identity and time | `deviceId` and `receivedAt` come from what the platform authenticated and when IoT Hub enqueued the message — never from the payload. |
| Transport | HTTPS only; a plain-HTTP gateway address is refused before a token is even requested. Loopback is the only exception. |
| Retries | `408`, `429`, `5xx`, network errors and timeouts are retried with exponential backoff and jitter, honouring `Retry-After`: the standard pipeline from `Microsoft.Extensions.Http.Resilience` — three retries, 10 seconds per attempt, 30 seconds in total by default. Any other `4xx` is never retried by that pipeline; `401` is retried once by the forwarder itself (see Token). |
| Token | Obtained with the client credentials grant, cached, and renewed five minutes before expiry (at most half its lifetime). Concurrent messages share one refresh. On a `401` the cached token is discarded and the same request — same body, same `Idempotency-Key` — is sent once more with a newly obtained token, so a signing-key rotation at the provider costs no message. That retry is a second pass through the resilience pipeline, with its own retries and its own 30-second budget, and there is never a third. A second `401` is logged at error level as an authentication failure, distinct from transport failures, and the message fails; the new token is kept, since another would fare no better. A `403` is not retried. |
| Failure | The message ends as `ProcessingOutcome.ForwardingFailed`, logged at error level with the message id, the device and the reason. |

> **A message that fails to forward is currently lost.** Nothing downstream has it and the
> platform does not keep it; the log line is the only record. Durable dead-lettering is
> [#19](https://github.com/asnielsen789/IIoT-platform/issues/19).

### Configuration

App settings, with `__` between section and key as Azure app settings and environment variables
spell it:

| Setting | Example | Notes |
|---------|---------|-------|
| `GatewayV1__BaseAddress` | `https://gateway.example.com/` | HTTPS. Requests go to `{BaseAddress}/v1/readings`. |
| `GatewayV1__Resilience__…` | `GatewayV1__Resilience__Retry__MaxRetryAttempts=5` | Optional. Binds onto the standard resilience options. |
| `GatewayAuth__TokenEndpoint` | `https://<tenant>.<region>.auth0.com/oauth/token` | HTTPS: the client secret is sent here. |
| `GatewayAuth__ClientId` | | |
| `GatewayAuth__ClientSecret` | `@Microsoft.KeyVault(SecretUri=…)` | **Secret.** A Key Vault reference in Azure; `local.settings.json` (gitignored) for local runs. Never in a committed file, never logged. |
| `GatewayAuth__Audience` | the gateway API's identifier | Auth0 requires it. |
| `GatewayAuth__Scope` | | Optional, for providers that identify the API by scope instead. |
| `GatewayAuth__ClientAuthentication` | `ClientSecretPost` | Or `ClientSecretBasic` (HTTP Basic). Default `ClientSecretPost`, Auth0's default. |
| `GatewayAuth__RenewalMargin` | `00:05:00` | Optional. |

None of these is validated at startup. Until a gateway exists the Function keeps validating and
logging device traffic, and every valid message ends as `ForwardingFailed` with the missing
setting named in the log.

---

## 10. Open items

| Item | Resolved by |
|------|-------------|
| An Auth0 tenant, an API whose identifier is the audience, and a machine-to-machine application authorised for it — the real client id and secret | [#12](https://github.com/asnielsen789/IIoT-platform/issues/12) |
| The client secret in Key Vault, `GatewayAuth__ClientSecret` as a Key Vault reference, and the other settings in the infrastructure templates | [#12](https://github.com/asnielsen789/IIoT-platform/issues/12) |
| The gateway mock configured with the tenant as its `Authority` | [#12](https://github.com/asnielsen789/IIoT-platform/issues/12) |
| A gateway the deployed Function can reach over HTTPS — the mock hosted somewhere, or exposed through a tunnel — and a way to see what it received beyond its log | [#15](https://github.com/asnielsen789/IIoT-platform/issues/15) |
| Durable dead-lettering of messages that fail to forward | [#19](https://github.com/asnielsen789/IIoT-platform/issues/19) |
