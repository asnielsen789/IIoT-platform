# ADR-008: Downstream contract and forwarding context

## Status

Accepted

## Context

The platform forwards validated telemetry to a downstream gateway. ADR-005 removed the only
real target, so there is no external specification to implement: the platform has to define
what it delivers. Building that path (#13, #14, and the provider-independent part of #12)
raised two questions that shape everything after it.

**Who owns a reading once it leaves the platform.** `TelemetryService` verifies the device's
identity against the IoT Hub system property `iothub-connection-device-id`, but it forwarded
only the message. The message's `deviceId` is optional and informational (see the trust model
in `docs/mqtt-payload.md`). A forwarder could therefore receive readings with no trustworthy
owner, and in the minimal case with no owner at all.

**What the downstream format is.** The device contract evolves on the device's timescale, which
is years and over the air (ADR-004). A downstream system should not have to follow it.

Event Hubs delivers at least once, so the gateway will see duplicates as a matter of course.

## Options

**Identity:**

1. **Explicit context.** `IDataForwarder.ForwardAsync` takes a `ForwardingContext` alongside the
   message: the authenticated device identity and IoT Hub's enqueued time, which the platform
   stamps and which can be trusted, unlike the device clock.
2. **Overwrite `deviceId`.** After the identity check, replace the message's `deviceId` with the
   authenticated value. No interface change, but the same field would mean "the device's claim"
   before the check and "verified identity" after it, and there is no place for platform time.

**Format:**

1. **An own, independently versioned contract.** The forwarder translates the device message
   into a downstream format with its own version.
2. **Pass the device message through.** Less code, but every downstream system then depends on
   the device contract directly, and the adapter has nothing to adapt.

## Decision

**Explicit context, and an own versioned contract.**

- `ForwardingContext(AuthenticatedDeviceId, EnqueuedAt)` is built only after the identity check
  has passed. The forwarded `deviceId` and `receivedAt` come from the context, never from the
  payload. The trust model, its rejection outcomes and its logging are unchanged.
- The downstream contract is `POST /v1/readings`, published as a JSON Schema with examples in
  `docs/schemas/gateway/` and described in `docs/gateway-contract.md`. Both sides are tested
  against the same schema, the way the device contract is.
- **Idempotency is keyed on the pair `(deviceId, Idempotency-Key)`**, with the key equal to the
  device's `messageId`. The device contract promises only that `messageId` is unique per
  device; keying on the id alone would let one device's message be discarded as another's
  duplicate, or let a device suppress another's readings by reusing its ids. The first delivery
  wins and a replay receives the same answer.
- **The downstream contract is stricter than the device contract.** Adding a reading `kind`
  requires `/v2`, whereas the device contract may add one without a version change. Here the
  platform is the producer, and it cannot assume every receiver is updated first.
- **Data minimisation** (CRA Annex I Part I point (g)): battery, signal, `sentAt`, `sequence`
  and `schemaVersion` are not forwarded. Downstream needs the readings and the firmware version,
  not the device's operational telemetry.
- **Authentication** is OAuth2 client credentials (RFC 6749 section 4.4) through a
  provider-independent token provider in `IIoT.Core`: tokens are cached, renewed before expiry
  and shared between concurrent callers. The gateway mock accepts only RS256-signed tokens and
  validates issuer, audience and lifetime on every endpoint. Auth0 is configuration, not code
  (#12).

## Consequences

- **Two contracts to version instead of one.** The translation in `GatewayV1Forwarder` is the
  cost, and also the point: a device-schema change stops at the platform unless the downstream
  contract is deliberately changed too.
- **Forwarding failures are visible but not yet durable.** Transient failures (5xx, 408, 429,
  timeouts) are retried; other client errors are not. A final failure is recorded as
  `ProcessingOutcome.ForwardingFailed` and logged, but the message is then lost: per Microsoft's
  documentation, *"Function executions that raise an exception continue to progress the
  pointer"*, so rethrowing would not bring it back either. Durable dead-lettering is #19.
- **No startup validation of the gateway settings in the Function.** Refusing to start without a
  configured gateway would block the end-to-end test of validation and identity (#10) until a
  gateway exists. An unconfigured or insecure setting instead fails each forwarding attempt,
  naming the setting in the log, while validation keeps working. The gateway mock, by contrast,
  refuses to start with an incomplete or insecure configuration.
- **HTTPS only.** Both the forwarder and the token provider refuse a plain-HTTP endpoint unless
  it is a loopback address, which the in-process tests need.
- **`TelemetryService` became scoped** to a function invocation. As a singleton it would have
  held the first forwarder and its `HttpClient` for the life of the worker.
- **Still open:** a real identity provider and secrets in Key Vault (#12), and a gateway reachable
  from Azure for the end-to-end test with authenticated devices (#15).

## References

- [ADR-004](adr-004-message-format.md) — the forwarder takes the whole message; batching is
  preserved
- [ADR-005](adr-005-scope-generalisation.md) — no external downstream specification
- [ADR-007](adr-007-telemetry-stream-access.md) — how the Function reads the telemetry stream
- `docs/gateway-contract.md` and `docs/schemas/gateway/` — the contract itself
- [Azure Functions reliable event processing](https://learn.microsoft.com/azure/azure-functions/functions-reliable-event-processing)
  — checkpoint behaviour on exceptions
- Hardt, D. (2012). *The OAuth 2.0 Authorization Framework* (RFC 6749). IETF
