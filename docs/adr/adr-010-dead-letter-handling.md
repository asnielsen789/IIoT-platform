# ADR-010: Dead-letter handling for messages that fail to forward

## Status

Accepted

## Context

A message that passes validation and the identity check is forwarded to the downstream gateway
(ADR-008). The forwarder retries transient failures with backoff, and a `401` once with a new
token, but when those retries are spent the message ends as `ProcessingOutcome.ForwardingFailed`,
is logged, and is lost. ADR-008 recorded that as its open consequence: rethrowing would not bring
the message back, because Microsoft documents that *"Function executions that raise an exception
continue to progress the pointer"*, and *"Durable dead-lettering is #19"*.

The messages at stake are the valuable ones. A gateway outage of an hour costs every reading the
fleet sends in that hour, including buffered backfill that devices only send because an earlier
outage made them hold it. Microsoft's guidance for Event Hubs triggers describes the pattern
directly: retry a few times, and *"if the eventual outcome of all retries is a failure, then add an
event to a queue so processing can continue on the stream"*
([Azure Functions reliable event processing](https://learn.microsoft.com/azure/azure-functions/functions-reliable-event-processing)).

Three questions follow: what is kept, where, and how it is retried without losing a message,
storing it twice, or circling forever.

Facts that constrain the design:

- **A Storage queue message is at most 64 KiB once encoded** (*"The encoded message can be up to
  64 kibibytes (KiB) in size"*, [Put Message](https://learn.microsoft.com/rest/api/storageservices/put-message)),
  and the Functions queue trigger expects base64 by default, which writes every 3 bytes as 4
  characters. An entry may therefore be 49,152 bytes.
- **The telemetry schema does not bound a message's size.** `additionalProperties` is `true` at
  every level, and a number or a timestamp's fraction may have any number of digits. Only IoT Hub
  does, at 256 KB per device-to-cloud message
  ([IoT Hub quotas](https://learn.microsoft.com/azure/iot-hub/iot-hub-devguide-quotas-throttling#other-limits)).
- **A queue message expires after seven days by default** (*"If this parameter is omitted, the
  default time-to-live is 7 days"*, Put Message), unless it is written with `-1`, which *"indicates
  that the message doesn't expire"*.
- **The queue trigger's retry is configurable and documented.** On failure the host makes the
  message invisible for `visibilityTimeout`, *"the time interval between retries when processing of
  a message fails"*, and after `maxDequeueCount` attempts, *"the number of times to try processing a
  message before moving it to the poison queue"*, moves it to `<queue>-poison`
  ([host.json settings](https://learn.microsoft.com/azure/azure-functions/functions-bindings-storage-queue#host-json);
  [Poison messages](https://learn.microsoft.com/azure/azure-functions/functions-bindings-storage-queue-trigger#poison-messages)).
  The extension's source confirms the mechanics: a failed message is released with Update Message,
  a poison move adds the message to the poison queue (creating the queue only if it is missing)
  and then deletes the original (`QueueProcessor` in `Microsoft.Azure.WebJobs.Extensions.Storage.Common`,
  Azure/azure-sdk-for-net).

## Options

**What is dead-lettered:**

1. **Forwarding failures only.** Valid and authenticated messages that could not be delivered.
2. **Every message that was not delivered**, rejections included.

**Where:**

1. **A Storage queue in the existing storage account.** No new resource; a built-in Functions
   trigger with retry and poison handling; identity-based access like the host storage.
2. **A Service Bus queue.** Native dead-lettering, sessions and larger messages, but a new paid
   resource (Basic bills per operation, Standard per hour), and an Azure-specific messaging feature
   where the project prefers open mechanisms (ADR-005).
3. **Blobs.** Any size, cheap, but no retry, visibility or poison mechanics: a retry scheduler
   would have to be built, and a timer that lists and leases blobs is exactly the machinery a queue
   trigger already is.

**How it is retried:**

1. **A queue-triggered function, with the trigger's own schedule.**
2. **A timer-triggered sweep** that reads the queue and forwards what it finds.
3. **A Functions retry policy on the Event Hubs trigger**, retrying the whole invocation in place.

## Decision

**Forwarding failures only, in a Storage queue, retried by a queue-triggered function.**

**What.** Only `ForwardingFailed` is dead-lettered: transport errors, `5xx`/`408`/`429` after the
resilience pipeline's retries, a second `401`, and a gateway that refuses the batch. A refusal
(`400`, `403`) is kept too: it is a disagreement between the platform and the gateway that a fix on
either side can resolve, and keeping the message lets it be delivered afterwards. Rejections are
not dead-lettered. An invalid payload or an identity mismatch fails identically on every attempt,
and both are already logged, the mismatch as a security event.

**Where.** The queue `forwarding-failed` and its poison queue `forwarding-failed-poison`, both
declared in `infra/modules/storage.bicep`, so nothing creates a queue at run time.

- **Access** is identity-based, with the Function's user-assigned identity, through a connection of
  its own: `DeadLetterStorage__queueServiceUri`, `__credential`, `__clientId`. Staging points it at
  the host storage account; moving the queues elsewhere is a change of settings and roles.
- **Roles**, each on one queue, the narrowest scope Azure RBAC offers for queue data:
  - `forwarding-failed`: **Storage Queue Data Contributor** (`974c5e8b-45b9-4653-ba55-5f855dd0fb88`).
    Microsoft lists Storage Queue Data Reader plus Message Processor for a queue trigger and
    Contributor or Message Sender for output
    ([Grant permissions to an identity](https://learn.microsoft.com/azure/azure-functions/manage-connections?pivots=functions-auth-identity&tabs=bindings#grant-permissions-to-an-identity)).
    The trigger also releases a failed message with Update Message, which needs `messages/write`
    ([Permissions for calling data operations](https://learn.microsoft.com/rest/api/storageservices/authorize-with-azure-active-directory#permissions-for-queue-service-operations)),
    and no built-in queue role but Contributor grants it. Contributor covers the writer's Put Message
    too.
  - `forwarding-failed-poison`: **Storage Queue Data Message Sender**
    (`c6a89b2d-59bc-44d0-9896-0f6e12d7b80a`), `messages/add/action` only: the runtime's poison move
    and the function's own both just add. Nothing in the app reads the poison queue.

**The entry** is a versioned JSON envelope, `envelopeVersion: 1`, in IIoT.Core
(`DeadLetterEnvelope`):

| Field | Why |
|---|---|
| `messageId` | Identifies the entry without decoding the payload; it is also the `Idempotency-Key` |
| `authenticatedDeviceId` | `iothub-connection-device-id` of the original delivery, the authoritative identity. A retry cannot establish it again: the transport that proved it is gone |
| `enqueuedAt` | IoT Hub's enqueued time, forwarded again as `receivedAt` |
| `deadLetteredAt` | When the first attempt ended |
| `failure.reason`, `failure.statusCode` | Why the first attempt failed, and the gateway's final status if it answered. The reason is capped at 1,000 characters |
| `payload` | The message body exactly as received, as a JSON string so that it is kept character for character. `schemaVersion` travels inside it (ADR-004) |

The size limit is checked before writing. A realistic worst case is far below it: 64
measurements with every field at a realistic maximum, the longest `messageId` and device ID, and
full metadata come to an 8.0 KB payload and a 10.6 KB entry, 14.1 KB as base64. The fixed part of an
entry is at most about 1.4 KB with ASCII identifiers. Escaping at most doubles an ASCII payload (a
quote becomes `\"`) and at most triples any payload (a 4-byte character may be written as a 12-byte
escaped surrogate pair), so a payload of up to about 23 KB of ASCII, or about 15 KB of anything,
always fits, and typical JSON, which escaping enlarges by about 15 %, fits up to about 40 KB. Above that, which only a payload
padded far beyond the contract reaches, the entry is **refused, not truncated**, and the message is
logged at Critical: a truncated payload would fail validation on every retry.

**The retry** is `RetryForwarding`, a queue trigger on `forwarding-failed`:

- **The same path as the first attempt.** The stored payload is validated again, cross-checked
  against the stored authenticated identity, and forwarded with the stored enqueued time. The
  forwarder therefore builds the same batch with the same `Idempotency-Key`, and a gateway that
  stored it on an attempt whose answer was lost acknowledges the retry as a replay (ADR-008).
- **The trigger's schedule**, in `host.json`: `maxDequeueCount` 8 and `visibilityTimeout`
  00:30:00. The writer makes the first entry invisible for one interval too, so the first retry does
  not follow straight on a failure the forwarder has just spent its own retries on; the eight
  attempts fall 30 minutes to four hours after the failure.
- **A failed retry throws**, so the queue keeps the entry for the next attempt. It is never
  dead-lettered again: an entry only ever moves forward, from the retry queue to the poison queue.
- **The last failed attempt moves the entry to the poison queue itself**, with no expiry. The
  runtime would move it too, but writes it with the seven-day default, after which an entry an
  operator had not reached would vanish. If the function's move fails, it throws, and the runtime's
  move remains the fallback.
- **A permanent outcome goes straight to the poison queue**: a payload rejected on re-validation
  (after a contract change, or an altered entry), or an entry that cannot be read (malformed, or a
  later `envelopeVersion`). Another attempt would end the same way. The entry is moved unchanged,
  never dropped, and the reason is logged.
- **Each attempt is logged** with its number, the dequeue count, and a delivery with the attempt
  that succeeded.

**If the dead-letter write fails**, `ProcessTelemetry` logs at Critical with the `messageId`, the
device and the forwarding failure, and returns. The message is then lost, as every failed message
was before this decision; that is the remaining edge. Rethrowing would not help. Without a retry
policy, the Event Hubs trigger advances its checkpoint past a failed invocation anyway (*"Function
executions that raise an exception continue to progress the pointer"*). With one, the host would
re-run the whole invocation — validation, the forwarder's full retry budget, and the write — while
the partition waits behind it; and the likeliest reason for the write to fail, Storage being
unavailable or refusing the identity, would hold every device's messages on that partition until
the policy gave up, after which the pointer advances regardless. The Storage client's own retries
already cover a transient fault in the write.

**Inspection and replay** stay manual: `infra/README.md` documents reading the poison queue with
`az storage message peek` and `get`, and putting an entry back on `forwarding-failed` with `put`,
with the data-plane roles the operator needs. No tool is built for it.

Service Bus was rejected for its cost and its lock-in, not its capabilities: its dead-letter queue
would replace the poison-queue mechanics, but as a new resource with a recurring price in a project
with a 100 DKK monthly budget, and as an Azure-specific feature the platform would come to rely on.
A Storage queue is a plain list of text messages in an account that already exists, which any other
queue can replace when the component moves. Blobs were rejected because the retry mechanics would
have to be written. A timer sweep
would duplicate the trigger's scheduling and give up its per-message visibility and dequeue count.
A retry policy on the Event Hubs trigger holds the partition, as above.

## Consequences

- **A gateway outage of up to about four hours costs no message.** A longer one moves the entries
  to the poison queue, where they wait, without expiry, for an operator.
- **Messages can arrive downstream out of order and late.** A retried batch carries its original
  `receivedAt`, and a receiver must already accept late, buffered readings (gateway contract,
  section 3), so the contract does not change.
- **Write access to `forwarding-failed` is the authority to assert a device's identity.** A retry
  forwards under the `authenticatedDeviceId` the entry carries, so anyone who can add an entry can
  file readings for any device. Only the Function's identity holds that right, and an operator only
  for a replay; signing entries, so a forged one is refused, is a candidate for the security review
  ([#21](https://github.com/asnielsen789/IIoT-platform/issues/21)), as is a custom role narrower
  than Contributor on that queue.
- **Readings are kept in the platform's storage** until delivered or handled, payload and metadata
  included. Delivered entries are deleted; poison entries stay until an operator deletes them. That
  is data the platform already holds in its logs, kept for its stated purpose (CRA Annex I Part I
  point (g)).
- **A queue listener that cannot authenticate takes the host down.** The extension backs off on a
  missing queue or a server error, but rethrows anything else, a `403` or a token that cannot be
  obtained included (`QueueListener`, same source), and the host shuts down and is restarted. After
  the first deployment, until the new role assignments have propagated, which Microsoft says can
  take up to 30 minutes, the host may restart repeatedly; telemetry waits in IoT Hub and is processed
  from the checkpoint. A misconfigured identity would do the same permanently, which the templates
  rule out by wiring `__clientId` from the identity that host storage already uses.
- **Missing settings disable only the retry.** Without the `DeadLetterStorage` settings the worker
  still starts: `RetryForwarding` fails indexing and is disabled, and every dead-letter write is
  logged at Critical naming the setting, while `ProcessTelemetry` keeps processing.
- **One more host extension outside the SBOM.** The worker package
  `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` adds
  `Microsoft.Azure.WebJobs.Extensions.Storage.Queues` to the extensions project that
  `Azure.Functions.Sdk` generates, which no SBOM and no Dependabot run sees
  ([#35](https://github.com/asnielsen789/IIoT-platform/issues/35)).
- **The attempt limit is stated twice**, in `host.json` and as the default of
  `ForwardingRetrySchedule`, because the function must recognise its last attempt. A test compares
  the two, and an app-setting override of `host.json` reaches the code too.
- **Cost is negligible.** Queue operations cost US$0.004 per 10,000 in North Europe; idle polling
  is at most one a minute while an instance runs.

## References

- [ADR-004](adr-004-message-format.md) — the version travels in the body, into a dead-letter queue
- [ADR-005](adr-005-scope-generalisation.md) — open mechanisms over proprietary ones
- [ADR-007](adr-007-telemetry-stream-access.md) — the Function's identity and the host storage account
- [ADR-008](adr-008-downstream-contract.md) — idempotency on `(deviceId, Idempotency-Key)`, and the
  consequence this decision closes
- [Azure Functions reliable event processing](https://learn.microsoft.com/azure/azure-functions/functions-reliable-event-processing)
- [Azure Queue storage trigger and bindings: host.json settings](https://learn.microsoft.com/azure/azure-functions/functions-bindings-storage-queue#host-json)
- [Azure Queue storage trigger: poison messages and peek lock](https://learn.microsoft.com/azure/azure-functions/functions-bindings-storage-queue-trigger#poison-messages)
- [Configure connections in Azure Functions: grant permissions to an identity](https://learn.microsoft.com/azure/azure-functions/manage-connections?pivots=functions-auth-identity&tabs=bindings#grant-permissions-to-an-identity)
- [Azure built-in roles for Storage](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/storage) — role definitions and IDs
- [Authorize with Microsoft Entra ID: permissions for queue service operations](https://learn.microsoft.com/rest/api/storageservices/authorize-with-azure-active-directory#permissions-for-queue-service-operations)
- [Authorize access to queues using Microsoft Entra ID: resource scope](https://learn.microsoft.com/azure/storage/queues/authorize-access-azure-active-directory#resource-scope)
- [Put Message](https://learn.microsoft.com/rest/api/storageservices/put-message) — size limit and time-to-live
- Azure/azure-sdk-for-net, `sdk/storage/Microsoft.Azure.WebJobs.Extensions.Storage.Common/src/Shared/Queues/`
  (`QueueProcessor.cs`, `QueueListener.cs`) — release, poison and listener behaviour
