# ADR-012: The gateway mock keeps what it accepts in Blob Storage

## Status

Accepted

## Context

The gateway mock (`tools/IIoT.GatewayMock`) is the platform's downstream receiver in staging. It
holds what it accepts in memory. A restart empties it, and on App Service F1 restarts are routine:
the app unloads after about 20 idle minutes, and the next request starts it again. The dashboard
(#47) is then empty until new messages arrive, and the reference fleet's history (#79) is gone.
Issue [#89](https://github.com/asnielsen789/IIoT-platform/issues/89) asks for readings that survive
that.

Constraints:

- **The platform stores no readings.** It validates and forwards (ADR-005, ADR-008). Retention
  belongs to the receiver, which in staging is the mock. Nothing here changes the Function.
- **Cost cap 100 DKK a month.** The reference fleet sends about 120 messages a day from 20 devices.
- **Open formats and an easy hand-over.** An operator should be able to take the data elsewhere
  with ordinary tools.
- **Idempotency.** The mock acknowledges a replay (same device, same `Idempotency-Key`) without
  storing it twice. In memory this only holds until the next restart, while the platform's
  dead-lettering (ADR-010) replays for up to four hours.

## Options

1. **Keep the mock in memory**, the baseline. It costs nothing and does not meet the goal.
2. **Azure Table Storage.** It costs little and has simple keys (device, message ID). But a batch is
   a JSON document, which would sit in one string property, and Table Storage has no time-to-live,
   so retention would be code.
3. **Azure Cosmos DB, free tier.** Its free allowance is one account per subscription. It adds a
   proprietary API and a database to manage, for a store whose only queries are "write if absent"
   and "read everything".
4. **Azure Database for PostgreSQL or SQL Database.** These are a server to run and keep patched,
   with their own cost or free-offer limits to watch. The mock needs no relational query.
5. **Blob Storage: one JSON file per batch.** Plain files, readable with any tool. Lifecycle
   management deletes old ones at no cost, and a conditional create gives idempotency without
   anything else.
6. **IoT Hub message routing to a storage endpoint**, archiving the raw device messages. This would
   make the platform store readings, against ADR-005 and ADR-008, and it would archive messages
   before they are validated. Rejected.

## Decision

**Option 5.** The mock writes each batch it accepts to Blob Storage, and acknowledges it only once
it is written.

- **What is kept.** The request body, byte for byte, as one block blob named
  `<deviceId>/<Idempotency-Key>.json`, with the content type `application/json`. Each part of the
  name is percent-encoded, dots included. The name therefore has exactly one slash, no `.` or `..`
  segment that a URL would resolve away, and no device can write into another device's folder. The
  usual IDs, such as `tank-000001` and a GUID, are unchanged. Two metadata entries record who
  delivered the batch (the token's `sub`) and when the mock accepted it.
- **Idempotency.** A write is a create, sent with `If-None-Match: *`, so the service refuses it
  when the blob exists. The first delivery wins, a replay after a restart is recognised without
  anything in memory, and two instances racing on one key cannot both store it. When an archive is
  configured, it decides what counts as a replay. Memory decides only when there is no archive.
- **When storage fails.** The mock answers `503` and stores nothing, so the sender keeps the batch.
  The platform retries `5xx` and then dead-letters (ADR-010), as it would for any receiver that
  cannot store. A `202` always means "stored".
- **Reading it back.** Memory, which the dashboard reads, is filled from the archive the first
  time someone opens the dashboard after a restart, not at startup. The hosted mock restarts after
  every idle spell, mostly to take one delivery. Reading the archive back on each of those starts
  would cost one read per stored batch and serve no one. Until the dashboard asks, deliveries are
  archived and acknowledged as usual. Batches read back are placed before those the process
  accepted itself, oldest first. If the read fails, the dashboard shows what memory holds, says
  so, and tries again on the next request.
- **Where.** A storage account of the mock's own (`stiiotstagingsdcmock<suffix>`), in the mock's
  region, created by the mock's module. It is not a container in the platform's account, because
  the Function's identity has Storage Blob Data Owner on that whole account, and the receiver's
  data must not be readable by the platform. An operator who drops the mock's module drops its
  data with it. The account has the same settings as the platform's: Microsoft Entra ID only,
  shared keys disabled, no public blobs, TLS 1.2.
- **Access.** The mock's system-assigned identity has Storage Blob Data Contributor on the one
  container (`readings`). This is the narrowest built-in role that can write a blob. It also
  allows deletes and overwrites, which the mock never makes. A custom role without them is a
  candidate for the security review (#21).
- **Retention: 30 days**, set by the parameter `gatewayMockArchiveRetentionDays`. A lifecycle rule
  deletes blobs older than that, about once a day. Thirty days is enough for demonstrations and the
  dashboard's trends, and it bounds what a restart reads back.
- **Opt-in.** The setting `GatewayMock:Archive:ContainerUri` switches the archive on. Without it
  the mock stays in memory, so tests and local runs need no storage account. The templates always
  set it.

### Cost

From Azure's retail prices for Hot LRS block blobs in Sweden Central on 2026-11-13: writes cost
0.3355 DKK per 10,000, reads 0.0268 DKK per 10,000, listings 0.3355 DKK per 10,000, and storage
0.1235 DKK per GB a month. A storage account has no fixed charge.

| At the reference fleet's volume | Per month |
|---|---|
| Writes: about 120 a day, so about 3,650 | about 0.12 DKK |
| Data: 30 days × 120 × about 1.5 KB, so about 5.4 MB | under 0.001 DKK |
| Reading back after a restart: about 3,600 reads and one listing | about 0.01 DKK each time |

The total is well under 1 DKK a month at normal use. The one cost that grows with use is reading
back, which scales with how often the dashboard is opened after the mock has unloaded, not with
time. Ten times a day would be about 3 DKK a month. The Function's calls already cross from North
Europe to Sweden Central, and the archive adds no transfer between regions, since it sits next to
the mock.

## Consequences

- The dashboard survives restarts, within the retention period, and so does idempotency.
- The archive is a set of plain files. An operator can download it with `az storage blob
  download-batch` or AzCopy, replay a batch with `curl`, or point another system at the
  container. Moving it means copying the container and setting `ContainerUri`.
- The mock now depends on `Azure.Storage.Blobs` and `Azure.Identity`, used only when the archive is
  configured.
- The first dashboard page after a restart waits while the archive is read back: 16 downloads in
  parallel, a few thousand small blobs at the fleet's volume. Later pages read memory.
- A storage outage is a forwarding failure on the platform's side, not a silent loss. Right after
  a deployment that creates the role, writes are refused until the assignment has propagated.
  They answer `503`, and the platform delivers again.
- The mock has an identity even with the dashboard off. Turning the dashboard off therefore no
  longer removes the identity, so its two Key Vault role assignments must be deleted by hand.
- One storage account more in staging: a seventh resource whose name must be globally unique.

## References

- [ADR-005](adr-005-scope-generalisation.md), [ADR-008](adr-008-downstream-contract.md),
  [ADR-010](adr-010-dead-letter-handling.md)
- `docs/gateway-contract.md`, section 5 (idempotency) and its status codes
- `tools/IIoT.GatewayMock/README.md`, "Archive"; `infra/README.md`, "Gateway mock"
- Microsoft Learn: [Specifying conditional headers for Blob service operations](https://learn.microsoft.com/rest/api/storageservices/specifying-conditional-headers-for-blob-service-operations),
  [Lifecycle management overview](https://learn.microsoft.com/azure/storage/blobs/lifecycle-management-overview),
  [Authorize access to blobs using Microsoft Entra ID](https://learn.microsoft.com/azure/storage/blobs/authorize-access-azure-active-directory)
- Azure retail prices API: `https://prices.azure.com/api/retail/prices`, filtered to Storage,
  `swedencentral`, `General Block Blob v2`, `Hot LRS`
- Issues: [#89](https://github.com/asnielsen789/IIoT-platform/issues/89),
  [#21](https://github.com/asnielsen789/IIoT-platform/issues/21)
