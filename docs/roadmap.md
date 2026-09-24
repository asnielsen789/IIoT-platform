# Roadmap — IoT Cloud Platform

## Working method

Agile and iterative. After an initial setup phase, design, implementation and test all run
within each iteration. Iterations are feature-based — each delivers a vertical slice of
functionality that can be demonstrated and tested.

```
Aug              Sep              Oct              Nov
 |                |                |                |
 |== Setup ======|== Iteration 1 ==|== Iteration 2 ==|== Iteration 3 ==|
 |  scope,        | MQTT pipeline   | Certificates,   | C2D, observ.,   |
 |  choices, IaC  | (sensor→Azure)  | auth, forwarder | hardening       |
```

---

## Setup (Aug 2026) `in progress`

Purpose: establish the project foundation so the first iteration can start cleanly.

- [x] Initial scoping — boundaries and architecture
- [x] Project context and technology choices documented (C#/.NET 10, GitHub Actions)
- [x] Backlog created with prioritised features
- [x] MQTT payload contract defined
- [ ] Provision an Azure subscription for development
- [ ] Azure resources provisioned via IaC (Bicep): IoT Hub, Key Vault, Function App
- [x] CI: format → build → test on pull requests and pushes to `main` — [#3](https://github.com/asnielsen789/IIoT-platform/issues/3)
- [x] CI: branch protection requiring the check before merge — [#3](https://github.com/asnielsen789/IIoT-platform/issues/3)
- [ ] CD: deploy to Azure — [#5](https://github.com/asnielsen789/IIoT-platform/issues/5)
- [ ] AI code review and security review integrated into CI — [#6](https://github.com/asnielsen789/IIoT-platform/issues/6)
- [ ] GitHub Projects integrated with CI/CD (issue linking, automatic status updates)

---

## Iteration 1 — MQTT pipeline (Sep 2026)

**Goal:** Sensor data from device to Azure Function, end to end.

| Activity | Description |
|----------|-------------|
| Design | Message format (MQTT payload schema), Function architecture, IoT Hub routing |
| Implementation | IoT Hub message routing → Azure Function trigger, deserialisation, validation |
| Test | MQTT test client (simulated device) sends readings, Function receives and logs |

**Demo criterion:** Simulated sensor sends a level reading → Function logs the correct payload.

---

## Iteration 2 — Security & downstream forwarding (Oct 2026)

**Goal:** An authenticated pipeline that delivers data in a downstream-compatible format.

| Activity | Description |
|----------|-------------|
| Design | Adapter interface, Auth0 client credentials flow, certificate strategy |
| Implementation | X.509 device provisioning via Key Vault, Auth0 JWT integration, gateway forwarder (adapter pattern) |
| Test | Device authenticates with a certificate, data is transformed and sent to a gateway mock with a valid JWT |

**Demo criterion:** Authenticated device → Azure → transformed data delivered to a gateway endpoint with a JWT.

---

## Iteration 3 — Cloud-to-device, observability & hardening (Nov 2026)

**Goal:** A complete platform with configuration management, monitoring and production-ready security.

| Activity | Description |
|----------|-------------|
| Design | C2D message flow, alerting strategy, security review |
| Implementation | Cloud-to-device configuration messages, Application Insights (logging, metrics, alerts), dead-letter handling |
| Test | Scale test (simulate a large fleet), AI-assisted security review, end-to-end with hardware if available |

**Demo criterion:** Full pipeline including C2D, a monitoring dashboard, and a documented security model.

---

## Backlog (unprioritised)

Features that may enter an iteration if capacity or need arises are listed once, in
[`issues.md`](issues.md#backlog-unprioritised). A second copy here is what allowed an item to
drift between the two lists.

---

## Dependencies

| Dependency | Owner | Status | Fallback |
|------------|-------|--------|----------|
| Azure subscription | Project | To provision | Local emulator for development |
| NB-IoT hardware | External vendor | Optional | Simulated MQTT client |
| Downstream gateway specification | Not established | Not required for v1 | Gateway mock |

Following [ADR-005](adr/adr-005-scope-generalisation.md), the platform no longer depends on
a third party for hardware, tenant access or interface documentation. Hardware validation is
deferred; a simulated device serves as the reference test harness.

## Risk assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Message contract diverges from real firmware | Medium | Medium | Additive evolution rules; contract validated against hardware if it becomes available |
| Azure free-tier limits exceeded during scale testing | Medium | Low | Scale tests bounded; throughput documented rather than maximised |
| Scope creep from speculative generality | Medium | Medium | MVP discipline — features added when a need is confirmed, not in anticipation |
| Solo project loses momentum between milestones | Medium | High | Kanban with WIP limit, milestone retrospectives (ADR-003) |
| Security regressions as the surface grows | Low | High | Security-by-design, SAST in CI, dedicated security review in Iteration 3 |

## Risk response

If hardware never becomes available:

1. **Simulated device:** MQTT test client sending realistic sensor data.
2. **Local emulator:** for development without a cloud tenant.
3. **Interface mock:** a gateway endpoint that validates JWTs and accepts the payload format.

These are the primary development path rather than contingencies — the platform is designed
to be demonstrable without physical hardware.

---

## Open items

- [ ] Provision an Azure subscription — [#1](https://github.com/asnielsen789/IIoT-platform/issues/1)
- [ ] End-to-end demonstration with a simulated device — [#10](https://github.com/asnielsen789/IIoT-platform/issues/10)
- [ ] Validate the message contract against real hardware (deferred) — [#22](https://github.com/asnielsen789/IIoT-platform/issues/22)
