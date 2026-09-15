# MQTT payload contract (v1)

> Normative schema: [`schemas/telemetry-v1.schema.json`](schemas/telemetry-v1.schema.json)
> Rationale: [ADR-004](adr/adr-004-message-format.md)
> Examples: [`schemas/examples/`](schemas/examples/)

This document is the contract between **device firmware** and the **IIoT platform**. It is
written as a proposal until it can be validated against real hardware.

Version 1 is deliberately an MVP. Fields are added additively once a need is confirmed —
not in anticipation of one.

---

## 1. Transport and topic

The device publishes to the fixed IoT Hub device topic:

```
devices/{deviceId}/messages/events/
```

`{deviceId}` must match the device's registered identity in IoT Hub.

### Required message properties

The device **must** set content type and encoding. IoT Hub can only route on message
content when both are present — without them, content-based routing fails silently and you
are limited to routing on application properties instead.

In MQTT these are set as a property bag on the topic:

```
devices/{deviceId}/messages/events/$.ct=application%2Fjson&$.ce=utf-8
```

| Property | Value | Required |
|----------|-------|----------|
| `$.ct` (contentType) | `application/json` | Yes |
| `$.ce` (contentEncoding) | `utf-8` | Yes |

---

## 2. Payload

```json
{
  "schemaVersion": 1,
  "messageId": "01J8F2K9QS4T7V0X3Z6B8N1M5B",
  "deviceId": "tank-000123",
  "sentAt": "2026-09-14T06:00:12Z",
  "sequence": 4711,
  "measurements": [
    {
      "type": "level",
      "value": 1432.5,
      "unit": "mm",
      "measuredAt": "2026-09-14T06:00:10Z",
      "quality": "ok"
    }
  ],
  "device": {
    "firmware": "1.4.2",
    "battery": { "value": 3.61, "unit": "V" },
    "signal": { "rsrp": -104, "rsrq": -11 }
  }
}
```

### Root fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `schemaVersion` | integer | Yes | Always `1` in this version. |
| `messageId` | string (≤64) | Yes | Unique per message. Used for idempotency on resend. |
| `deviceId` | string (≤128) | No | **Informational.** See section 4. |
| `sentAt` | ISO 8601 | Yes | Transmission time according to the device clock. |
| `sequence` | integer ≥0 | No | Monotonic per device. Gaps indicate lost messages. |
| `measurements` | array (1–64) | Yes | At least one element. |
| `device` | object | Yes | See section 3. |

### `measurements[]`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | `"level"` | Yes | Only `level` in v1. |
| `value` | number 0–20000 | Yes | **Raw** measured value. No conversion on the device. |
| `unit` | `"mm"` | Yes | UCUM code. |
| `measuredAt` | ISO 8601 | Yes | Time of measurement, not of transmission. |
| `quality` | `ok`/`uncertain`/`bad` | No | The device's own assessment. Defaults to `ok`. |

Multiple elements occur on **buffered transmission** after a network outage. Each element
carries its own `measuredAt`, so ordering and timing are preserved.

The upper bound of 20000 mm is a sanity check for the reference use case, not a
per-installation limit. Validation against actual vessel geometry belongs wherever that
geometry is registered.

---

## 3. `device` — metadata

Metadata is bounded by the CRA. See section 5 for the field-by-field rationale.

| Field | Type | Required | Rationale |
|-------|------|----------|-----------|
| `firmware` | string (≤32) | **Yes** | CRA Annex I Part II. See section 5. |
| `battery.value` / `.unit` | number / `"V"` | No | Operations: a large battery-powered fleet. |
| `signal.rsrp` | integer −140…−44 | No | NB-IoT diagnostics (dBm). |
| `signal.rsrq` | integer −20…−3 | No | NB-IoT diagnostics (dB). |

**Static attributes do not belong here.** Model, hardware revision, ICCID and vessel
geometry do not change between messages and should be registered in the IoT Hub **device
twin** rather than repeated every four hours. That follows directly from the data
minimisation requirement in section 5.

Semantic versioning is recommended for `firmware`, but the schema does not enforce it — the
vendor's actual versioning scheme is confirmed during hardware validation, and an
over-strict pattern would reject valid messages.

---

## 4. Trust model

**`deviceId` in the payload is not authoritative.**

IoT Hub stamps the authenticated identity onto the message as the system property
`iothub-connection-device-id`. It is derived from the device's X.509 certificate and cannot
be set by the device itself. The `deviceId` field in the body, by contrast, can contain
anything.

Without this check, a device holding a valid certificate could submit readings under
**another device's** identity, corrupting the data for that installation.

| Source | Trust | Use |
|--------|-------|-----|
| `iothub-connection-device-id` (system property) | Authoritative | All further processing |
| `deviceId` (payload) | None | Cross-check and logging |

The consumer **must** compare the two. On mismatch: log as a security event and reject the
message.

### Timestamps

Device clocks drift, and NB-IoT devices do not necessarily have a reliable RTC.

- `measuredAt` and `sentAt` come from the device and are **not** trustworthy as absolute time.
- IoT Hub's `enqueuedTime` is platform time and is trustworthy.
- A message must **never** be rejected because `measuredAt` lies far in the past. That is
  the normal shape of buffered backfill after an outage — precisely the data most worth
  keeping.
- The difference between `sentAt` and `enqueuedTime` is recorded as a metric so clock drift
  across the fleet can be detected.

---

## 5. CRA mapping

The CRA (Regulation (EU) 2024/2847) pulls in two directions on metadata. That is why
`device` is small and why every field has a stated reason.

| CRA requirement | Wording (extract) | Consequence for the schema |
|-----------------|-------------------|----------------------------|
| Annex I, Part I, point (e) | *"protect the confidentiality of stored, transmitted or otherwise processed data … by encrypting … in transit"* | Covered by MQTT over TLS. No encryption inside the payload. |
| Annex I, Part I, point (f) | *"protect the integrity of … data … against any manipulation or modification not authorised"* | Covered by mTLS with X.509. Hence **no** per-message signature in v1. |
| Annex I, Part I, point (g) | *"process only data … that are adequate, relevant and limited to what is necessary in relation to the intended purpose"* | **Constraining.** Only fields with a concrete operational or regulatory justification. Static attributes move to the device twin. |
| Annex I, Part I, point (l) | *"provide security related information by recording and monitoring relevant internal activity, including the access to or modification of data, services or functions"* | Acknowledged but **not** solved by this schema. See the note below. |
| Annex I, Part II, point 1 | *"identify and document vulnerabilities and components … including by drawing up a software bill of materials"* | `firmware` is the key linking a device in the field to the vendor's SBOM. |
| Annex I, Part II, point 2 | *"address and remediate vulnerabilities without delay, including by providing security updates"* | `firmware` makes it possible to identify exactly which devices run a vulnerable version. |
| Annex I, Part II, point 7 | *"provide for mechanisms to securely distribute updates … to ensure that vulnerabilities are fixed or mitigated in a timely manner"* | `firmware` is the verification that an OTA update actually landed on the device. |

Taken together, this is why `firmware` is the only **required** metadata field: without it
you can determine neither whether the fleet is vulnerable nor whether a remediation worked.

> **Note on security logging (point l).** A telemetry channel transmitting every four hours
> is the wrong transport for security events — an event must not wait four hours for the
> next window. Security-relevant logging should therefore be handled separately, either as
> a dedicated message type or via device twin reported properties. Addressed in the
> observability and security review work, not here.

> **Note on responsibility.** CRA obligations fall on the *manufacturer* of the product
> with digital elements. A platform may separately fall in scope as a "remote data
> processing solution" where it is necessary for the product to perform its function. That
> boundary should be confirmed legally and is not settled in this document.

---

## 6. Evolution rules

Devices are expected to remain in service for 10+ years, with firmware updated over the air
and rolled out gradually. Devices running different schema versions will therefore coexist
in the field for years.

**Additive changes** (no version bump):

- New optional fields
- New values in `measurements[].type`
- New optional fields in `device`

**Breaking changes** (require `schemaVersion: 2`):

- Removing or renaming a required field
- Changing a field's type or unit
- Tightening a constraint such that previously valid messages are rejected

**Consumer obligations:**

1. Unknown fields **must** be ignored, not rejected. `valid-unknown-fields.json` covers
   exactly this case.
2. An unknown `schemaVersion` must be rejected explicitly and logged — never interpreted
   as v1.
3. Both rules must be covered by tests so they are not lost in refactoring.

---

## 7. Examples

| File | Covers |
|------|--------|
| `valid-minimal.json` | Required fields only |
| `valid-full.json` | All fields populated |
| `valid-buffered-backfill.json` | Three readings after a network outage |
| `valid-unknown-fields.json` | Forward compatibility — unknown fields accepted |
| `invalid-missing-firmware.json` | CRA-required field absent |
| `invalid-wrong-unit.json` | `cm` instead of `mm` |
| `invalid-negative-level.json` | Negative measured value |
| `invalid-empty-measurements.json` | Empty `measurements` array |

These fixtures are **shared test ground**: the test client sends them, and validation checks
against them. The filename prefix — `valid` / `invalid` — states the expected outcome.

---

## 8. Open items

| Item | Resolved by |
|------|-------------|
| Actual payload from hardware versus this contract | Hardware validation, deferred — [#22](https://github.com/asnielsen789/IIoT-platform/issues/22) |
| The vendor's versioning format for `firmware` | Hardware validation, deferred — [#22](https://github.com/asnielsen789/IIoT-platform/issues/22) |
| Whether the sensor measures temperature | Hardware validation, deferred — [#22](https://github.com/asnielsen789/IIoT-platform/issues/22) |
| Vessel geometry and level → volume conversion | Not scoped |
| Security logging per Annex I point (l) | Observability — [#17](https://github.com/asnielsen789/IIoT-platform/issues/17); security review — [#21](https://github.com/asnielsen789/IIoT-platform/issues/21) |
| Legal scoping of CRA responsibility for the platform | Not scoped |
| Low-level alerting | Deferred — see below |

### Alerting — deferred

"Alert" covers three distinct problems, each with its own blocker. **None of them changes
the v1 schema**, and all three are deferred.

| Type | Detects | Requires | Status |
|------|---------|----------|--------|
| Predicted low level | Vessel runs empty in N days | Consumption history + geometry | Deferred |
| Sudden drop | Leak, theft, sensor fault | Rate of change in mm only | Deferred, not blocked |
| Device-initiated alarm | Device transmits outside its window | Firmware support | Deferred, vendor-dependent |

At a four-hour measurement interval, nothing can be detected faster than four hours unless
the device itself initiates. Since vendor dialogue is deferred, cloud-side detection gives up
no reaction time relative to what is actually achievable.

A device-initiated alarm would also place the decision at the least informed point: the
device knows neither vessel geometry, consumption history nor delivery schedule, and a
threshold in firmware requires an OTA campaign across the fleet to change. This is the same
trade-off that underpins the choice of raw measured values in ADR-004.

Deferring is free: the evolution rules in section 6 allow a new message type or a new
`measurements[].type` to be added additively without a version bump.

Note that platform alerting — error rate, dead letters, device disconnects — is a separate
concern. That is operational health, not a business event.
