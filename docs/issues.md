# Issues — IIoT Platform

Work is tracked in [GitHub Issues](https://github.com/asnielsen789/IIoT-platform/issues),
grouped by [milestone](https://github.com/asnielsen789/IIoT-platform/milestones). That is the
single source of truth for planned work — this file does not duplicate it.

---

## Backlog (unprioritised)

Ideas that have not been created as issues. When an item is picked up, it is created as a
GitHub issue and removed from this list, so the two never overlap.

> `B` numbers are local placeholders, **not** GitHub issue numbers. GitHub uses a single
> shared sequence for issues and pull requests, so a backlog item cannot reserve a number in
> advance.

| # | Title | Label | Description |
|---|-------|-------|-------------|
| B1 | Stub: GatewayV2Forwarder | feature | Empty adapter skeleton for a future gateway. Implemented once a specification exists. |
| B2 | Device twin state management | feature | Use IoT Hub device twins to track device state and desired configuration. |
| B3 | API: device fleet status | feature | Endpoint returning an overview of connected devices, latest reading, certificate expiry. |
| B4 | Automatic certificate renewal | feature | Key Vault-based rotation of device certificates without downtime. |
| B5 | OTA firmware updates | feature | Firmware distribution via IoT Hub. |
