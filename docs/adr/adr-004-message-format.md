# ADR-004: Message format and versioning for sensor telemetry

## Status

Accepted

## Context

NB-IoT sensors measure tank fill level and transmit a reading every four hours over MQTT
to Azure IoT Hub. Device firmware is produced by an external vendor, so the message
format is a **contract between two parties**, not an internal detail.

The platform is designed against the following assumptions, which drive the decisions
below:

| Assumption | Value |
|---|---|
| Fleet size | ~3000 devices |
| Transmission interval | Every 4 hours |
| Expected device service life | 10+ years |
| Firmware updates | Over the air, rolled out gradually |

A service life measured in decades means devices running different firmware versions —
and therefore different schema versions — will coexist in the field for years at a time.

The project is subject to the CRA (Regulation (EU) 2024/2847) and NIS2. The format is
defined as an MVP that can be extended additively as requirements are confirmed.

## Options

**Serialisation format**

1. **JSON** — Readable, universally tooled, verbose.
2. **CBOR** — Binary, roughly 40–60% smaller, requires tooling to inspect.
3. **Protobuf** — Compact and schema-driven, but requires a shared `.proto` and code
   generation at the vendor.

**Versioning**

1. Version in the payload body (`schemaVersion`).
2. Version in the MQTT topic.
3. No version — infer from which fields are present.

## Decision

**JSON with `schemaVersion` in the body. Additive evolution. Raw measured values.**

- Serialisation: JSON with readable field names.
- Versioning: an integer in the body, not in the topic.
- Measurement data: raw level in millimetres. No conversion to volume on the device.
- Temperature is not part of v1.
- Metadata: only what is operationally necessary or required by the CRA.

## Rationale

**JSON over a binary format.** Data volume is not a real constraint at this duty cycle:
3000 devices × 6 messages/day × ~250 bytes ≈ **4.5 MB/day** across the entire fleet. The
saving from CBOR is therefore measured in megabytes per month, while the cost is that
nobody can read a message without tooling — a genuine drawback when debugging against an
external vendor. Avoiding vendor lock-in also favours the most broadly supported format.

Note that the real cost on NB-IoT is *airtime and energy per message*, not aggregate
volume. At six short messages per day the difference between 150 and 250 bytes has no
practical effect on battery life. At a significantly higher duty cycle the arithmetic
changes, and this decision should be revisited.

**Version in the body rather than the topic.** The IoT Hub topic space is fixed
(`devices/{deviceId}/messages/events/`), and a version in both places would be two
sources of truth that can drift apart. A version in the body travels with the message
all the way through the pipeline — including into a dead-letter queue, where topic
context is gone.

**Additive evolution.** Given a 10+ year service life and over-the-air updates across a
large fleet, the fleet cannot be assumed to update as a unit. Consumers must therefore
ignore unknown fields rather than reject the message, and new fields must be optional.
Only changes that break existing consumers trigger a new `schemaVersion`.

**Raw measured values rather than derived volume.** Converting from level to volume
requires tank geometry and calibration. Placing that logic in firmware means any
correction to a misconfigured tank requires an OTA campaign against the device; placing
it in the cloud makes it a configuration change. Raw data also preserves the original
measurement for audit and recalculation — if a calibration turns out to be wrong, history
can be recomputed.

**Temperature omitted.** Liquid volume is temperature-dependent, and a temperature
reading would improve the accuracy of any later volume calculation. But no requirement
for it has been established, and it is unknown whether the sensor measures it at all. The
field can be added additively to `measurements[]` without a version bump once the need is
confirmed.

**Metadata bounded by the CRA.** See `docs/mqtt-payload.md` for the full mapping. In
short, the CRA pulls in two directions: Annex I, Part I, point (g) requires that only data
which is *"adequate, relevant and limited to what is necessary in relation to the intended
purpose"* be processed, while Part II, points 1, 2 and 7 presuppose that you can identify
which firmware is running where in order to remediate vulnerabilities and verify that
updates have landed. The result is that `firmware` is **required**, while static
attributes (model, hardware revision, ICCID) belong in the IoT Hub device twin rather than
in every single message.

## Consequences

- Messages are readable in the Azure Portal and in logs without decoding.
- Consumers **must** ignore unknown fields. A consumer that rejects unknown fields will
  break on the next additive extension.
- Volume calculation becomes a cloud responsibility and requires tank geometry to be
  registered per device. That is not solved in this ADR.
- The schema stands as a *proposal* to the device vendor until it can be validated against
  real hardware. Vendor dialogue is deferred, so the contract remains unconfirmed.
- `IDataForwarder` receives the **whole message**, not a single reading. A message may
  contain up to 64 measurements from buffered backfill, and the adapter needs device
  metadata in order to transform. This preserves batching downstream — one call rather
  than N.
- The schema file remains canonical in `docs/schemas/` and is embedded into `IIoT.Core` as
  a linked resource at build time. There is therefore only one copy, and the deployed
  Function validates against exactly the published version.
- At a significantly higher transmission frequency, the choice of JSON should be
  revisited.

## References

- European Parliament and Council. (2024). Regulation (EU) 2024/2847 (CRA), Annex I.
- `docs/mqtt-payload.md` — the contract
- `docs/schemas/telemetry-v1.schema.json` — normative schema
