# ADR-011: Device types beyond the tank, and gateway contract v2

## Status

Accepted

## Context

The platform is a generic reference architecture for industrial telemetry, and tank level
monitoring is only its worked example (ADR-005). So far, though, both contracts carry exactly one
measurement: `level` in `mm`. A platform that has only ever carried one kind of number has not shown
that it is generic.

[#79](https://github.com/asnielsen789/IIoT-platform/issues/79) adds three device types that an
operator of fuel stations, car washes and charging sites would run next to tanks:

- **EV charging stations**: energy delivered, current power, charging sessions.
- **Car washes**: water used, wash cycles, and the levels of their detergent, wax and water-reclaim
  tanks.
- **Oil separators**, which are required where vehicles are washed or refuelled: the thickness of the
  separated oil layer and the height of the sludge.

Facts that constrain the design:

- **The device contract allows this additively.** New values in `measurements[].type`, and new
  optional fields, need no `schemaVersion` bump (`docs/mqtt-payload.md`, section 6). The platform is
  the contract's only consumer and is updated before any device sends a new type.
- **The gateway contract does not.** Adding a reading `kind` is a breaking change on `/v1`
  (`docs/gateway-contract.md`, section 7). The platform is the producer there, and a receiver that
  validates against v1 would refuse the whole batch.
- **Some devices have several sensors of one type.** A car wash has three tanks, and an oil separator
  measures two layers. Their readings must not merge into one series.
- **Battery-powered devices lose messages and send late.** Buffered backfill is normal (ADR-004), and
  a message can be lost before the platform ever sees it.
- **Units are UCUM codes** in both contracts. In UCUM, kilowatt-hours are `kW.h`, and a pure count is
  `1`.

## Options

**Shape of the data:**

1. **A contract per device type**, each with its own fields. It is explicit, but every new type means
   new schemas and new code on both sides of the platform. That is the opposite of a generic platform.
2. **The existing measurement, generalised.** Measurements keep the shape of `type`, `value`, `unit`,
   `measuredAt` and `quality`, with more types and an optional `sensor`. A new type is a schema change
   and a mapping, not a new model.

**Counters:**

1. **Deltas,** such as "12 kWh since the last message". A lost message silently loses its delta, and
   a replayed one counts it twice.
2. **Cumulative totals,** the meter reading itself. A lost message costs nothing, because the next one
   carries the total. A replay changes nothing. Consumption over any period is the difference between
   two readings.

**Downstream:**

1. **Widen v1's `kind`.** That breaks every receiver that validates v1 strictly, which is exactly what
   the evolution rules exist to prevent.
2. **Gateway contract v2** at `/v2/readings`, served alongside `/v1`, and chosen by the platform's
   configuration per receiver.

## Decision

**The measurement is generalised, counters are cumulative, and the new kinds go downstream as
gateway contract v2.**

- **Five measurement types,** each fixing its unit and value range in the device schema:

  | Type | Unit | Range | Meaning |
  |---|---|---|---|
  | `level` | `mm` | 0–20000 | A level, as before |
  | `energy` | `kW.h` | 0–10⁹ | Cumulative energy meter reading |
  | `power` | `kW` | 0–1000 | Power at the time of measurement |
  | `volume` | `L` | 0–10¹² | Cumulative volume meter reading |
  | `count` | `1` | integer, 0–2⁵³−1 | Cumulative event counter |

  The upper bounds are sanity checks, as `level`'s 20000 mm already was, not limits of an
  installation.
- **An optional `sensor`** names which of a device's sensors or meters took a measurement: `oil` and
  `sludge`, or `shampoo`, `wax` and `reclaim`. It may contain lowercase letters, digits and hyphens,
  up to 32 characters, so it is safe in a log and in a URL. A device's series is its type and sensor
  together. A device with one sensor of a type leaves it out.
- **All of this is additive to device contract v1.** `schemaVersion` stays 1, and a device that sends
  only levels is unaffected. Every value range is per type now. Level keeps its own range, so nothing
  that was valid before becomes invalid.
- **Gateway contract v2** is v1 with the five kinds and the optional `sensor`, at `/v2/readings`, with
  `contractVersion: 2`. Everything else is unchanged: batching, idempotency, authentication and the
  receiver obligations. v1 stays as it is, and receivers move to v2 when they choose.
- **v1 refuses what it cannot carry.** A measurement of another type, or one that names its sensor,
  has no v1 mapping, and the message fails to forward with that reason. It is never sent without its
  other readings, and series are never merged. The platform sends one contract version, chosen by
  configuration (#79, step 3). Devices of the new types go only to receivers that speak v2.
- **Status is not telemetry.** A charger's connector state (free, charging, faulted) is a state, not a
  number with a time series. It belongs in the device twin's reported properties, like other
  non-measurement data (ADR-004), and is not part of this decision.

## Consequences

- **The platform shows it is generic.** Four device types share one model, one validator, one
  forwarder and one dashboard. Adding a fifth type is a row in the table above, a schema rule and a
  mapping.
- **Counters survive loss and replay.** That suits NB-IoT devices that buffer, and the
  at-least-once delivery between the platform and the gateway (ADR-008, ADR-010).
- **Receivers must derive consumption** from successive totals, and must handle a meter that is
  replaced or reset, where a total drops. That is the receiver's domain, as it is with any utility
  meter.
- **Two gateway contract versions to maintain,** for as long as any receiver stays on v1. The gateway
  mock serves both, and the fixtures of both are tested on both sides.
- **The validator's error reporting changes slightly.** The per-type rules are `if`/`then` pairs, and
  a failed `if` means only that a rule does not apply, so it is not reported. The platform's
  rejection reasons still name only the rule that was broken.
- **Thresholds are configuration, not telemetry.** An alarm when an oil layer reaches its limit is a
  value per installation. It is set through cloud-to-device configuration
  ([#16](https://github.com/asnielsen789/IIoT-platform/issues/16)) or applied downstream, not sent by
  the device as a measurement.

## References

- `docs/mqtt-payload.md`, sections 2 and 6: the device contract and its evolution rules
- `docs/gateway-contract.md`, sections 7 and 11: the gateway contract's evolution rules and v2
- `docs/schemas/telemetry-v1.schema.json`, `docs/schemas/gateway/readings-v2.schema.json`
- Schadow, G., & McDonald, C. J. *The Unified Code for Units of Measure* (UCUM). Regenstrief
  Institute. https://ucum.org/ucum
- [ADR-004](adr-004-message-format.md), [ADR-005](adr-005-scope-generalisation.md),
  [ADR-008](adr-008-downstream-contract.md), [ADR-010](adr-010-dead-letter-handling.md)
- Issues: [#79](https://github.com/asnielsen789/IIoT-platform/issues/79),
  [#16](https://github.com/asnielsen789/IIoT-platform/issues/16)
