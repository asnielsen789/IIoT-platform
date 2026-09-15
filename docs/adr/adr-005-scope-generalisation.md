# ADR-005: Generalisation of project scope

## Status

Accepted

## Context

The project began as the modernisation of an existing industrial IoT installation for a
specific customer, moving it to an NB-IoT and MQTT architecture on Azure. Several parts of
the roadmap depended on that customer — hardware delivery, access to their Azure tenant,
and documentation of their existing gateway interface.

Two factors made that dependency untenable:

1. **The dependencies blocked the critical path.** Hardware, tenant access and interface
   documentation blocked roughly half the roadmap. None of them were within the project's
   control, and none had a committed timeline.
2. **Generalisation was already the stated direction.** From the outset the intent was a
   generalised, scalable, vendor-independent prototype that could be adapted to a specific
   customer's requirements if they chose to proceed.

The project's goals were therefore restated: a well-engineered reference platform, usable
as academic work and as a public portfolio artefact, rather than a customer integration.

## Options

1. **Continue waiting for the customer.** Keeps the original scope, but leaves the
   critical path blocked by decisions outside the project's control, with no timeline.
2. **Generalise the platform, drop the customer framing.** Removes every external blocker.
   Loses validation against real hardware and a real downstream interface.
3. **Maintain both.** A generic core with a customer-specific integration alongside it.
   Highest ongoing cost, and the customer-specific half remains blocked regardless.

## Decision

**Generalise the platform and remove the customer framing entirely.**

The platform is presented as a generic industrial IoT telemetry reference architecture,
with tank level monitoring as the worked example rather than the subject.

Concretely:

- Design assumptions — fleet size, transmission interval, expected service life — are
  stated as requirements rather than as one customer's history.
- The downstream integration becomes a demonstration of the adapter pattern rather than an
  implementation against a specific gateway, and the forwarder is named
  after the format version it targets, `GatewayV1Forwarder`.
- Access to a customer tenant is replaced by a self-provisioned Azure subscription.
- Source material from the customer is removed from version control and preserved
  outside the repository.
- All documentation is written in English.

## Rationale

**The blockers disappear rather than being worked around.** Hardware, tenant access and
interface documentation stop being prerequisites. The single largest effect is that
tenant access becomes self-service: a personal Azure subscription with IoT Hub's free tier
comfortably handles a simulated device at six messages per day, which unblocks
infrastructure-as-code, message routing, end-to-end validation and observability.

**Very little is actually lost.** The gateway mock was always planned as a mock, and scale
testing was always going to be simulated. What is lost is validation against real hardware,
which was blocked in any case.

**The engineering rationale survives intact.** The decisions in ADR-004 rest on fleet size,
duty cycle and expected service life — not on who owns the devices. Restating those as
design assumptions preserves the reasoning while removing the dependency.

**A demonstrated capability beats a claimed one.** A platform that models one measurement
type properly, with documented evolution rules showing how others are added, is a stronger
engineering artefact than a speculative schema covering cases nobody has validated.

## Consequences

- Issues that depended on the customer are rescoped or closed. Tenant access is rescoped
  to a self-provisioned subscription; interface documentation is dropped.
- The repository is migrated to a new one seeded from a clean tree. The original repository
  is archived privately, retaining the full history and issue tracker.
- Git history is not rewritten. GitHub's `refs/pull/*` cannot be force-pushed, so commits
  referenced by existing pull requests would remain reachable regardless — history rewriting
  would not have achieved the goal.
- Documentation language changes from Danish to English, and commit messages follow.
- The measurement schema is unchanged. `level` in millimetres remains the only measurement
  type in v1; the additive evolution rules in ADR-004 are how other types are introduced.
- Validation against real hardware is deferred indefinitely. The message contract remains a
  proposal until it can be checked against a real device.

## References

- [ADR-003](adr-003-working-method.md) — working method
- [ADR-004](adr-004-message-format.md) — message format, whose assumptions this ADR restates
