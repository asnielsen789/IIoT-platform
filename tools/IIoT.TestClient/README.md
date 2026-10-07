# IIoT.TestClient — simulated NB-IoT device

A console application that stands in for device firmware. It publishes the contract's example
payloads to Azure IoT Hub over **raw MQTT/TLS**, so the platform can be exercised end to end
before any hardware exists.

> **Never commit a devices file, a device key or a connection string.** Everything secret is
> supplied at runtime — command line or environment — and this tool reads no configuration file
> other than the one you point `--devices` at. There is no `appsettings.json`, no user-secrets id
> and no gitignored-but-expected file. Keep your devices file outside the repository.

---

## Why raw MQTT and not the Azure IoT device SDK

Because the SDK would hide the thing being tested.

Section 1 of [`docs/mqtt-payload.md`](../../docs/mqtt-payload.md) requires the device to set
`contentType` and `contentEncoding` as a property bag on the publish topic. Without them, IoT Hub
cannot route on message content and **routing fails silently** — messages are accepted, nothing is
routed, and no error appears anywhere. The Azure device SDK sets those properties for you, so a
client built on it would pass whether or not the documented topic string is correct.

That string has never been verified against a real hub. This client is what verifies it. It is also
the honest simulation: the real device is a battery-powered NB-IoT sensor speaking MQTT through a
modem stack, not an application hosting an Azure SDK.

Transport library: [MQTTnet](https://github.com/dotnet/MQTTnet) 5.

## Why fixtures are replayed as bytes

The client does **not** reference `IIoT.Core`, and does not deserialise payloads into the
platform's models. The files in [`docs/schemas/examples/`](../../docs/schemas/examples) are read as
bytes and sent as bytes.

If the client round-tripped payloads through our own models, a bug in those models would be
invisible: the client and the Azure Function would agree with each other while both disagreed with
the published contract. The fixture files are the single arbiter.

The default `--mutate` behaviour edits the JSON tree — not typed models — so unknown fields survive
untouched and nothing the contract does not mention gets normalised away.

---

## What you need from Azure first

Nothing here works without a hub. In order:

1. **An IoT Hub.** Provisioned by the Bicep templates in `infra/`
   ([#9](https://github.com/asnielsen789/IIoT-platform/issues/9)). Note the hub name.
2. **A registered device identity.** The client authenticates as a device that exists in the hub's
   identity registry:
   ```
   az iot hub device-identity create --hub-name <hub> --device-id tank-000001
   ```
3. **That device's primary key:**
   ```
   az iot hub device-identity connection-string show \
     --hub-name <hub> --device-id tank-000001 --query connectionString -o tsv
   ```
   Pass only the `SharedAccessKey=` **value** as `--device-key`, not the whole connection string.
4. **Outbound TCP 8883.** Many corporate and campus networks block it. There is no WebSocket
   fallback in this client, because the real device does not have one either.

Device authentication is a shared access key for now. X.509 client certificates — the platform's
intended mechanism — are [#11](https://github.com/asnielsen789/IIoT-platform/issues/11); the
authentication seam (`IDeviceAuthentication`) exists so adding them does not restructure the
client.

---

## Running it

A single device, one message, no network at all:

```
dotnet run --project tools/IIoT.TestClient -- \
  --hub <hub> --device-id tank-000001 --device-key <key> --once --dry-run
```

`--dry-run` prints the username, the topic and the payload without connecting. Useful for reading
the property bag back character by character before there is a hub.

A single device publishing every four minutes, indefinitely:

```
dotnet run --project tools/IIoT.TestClient -- \
  --hub <hub> --device-id tank-000001 --device-key <key> --interval 240
```

Keys via the environment instead, so they stay out of shell history:

```
export IOT_HUB=<hub>
export DEVICE_ID=tank-000001
export DEVICE_KEY=<key>
dotnet run --project tools/IIoT.TestClient -- --once
```

Prove the pipeline rejects a bad message, with the exact bytes from the contract:

```
dotnet run --project tools/IIoT.TestClient -- \
  --hub <hub> --device-id tank-000001 --device-key <key> \
  --fixture invalid --no-mutate --count 1
```

Twenty devices at once, from a fleet file:

```
dotnet run --project tools/IIoT.TestClient -- \
  --hub <hub> --devices ~/.secrets/iiot-devices.json --device-count 20 --interval 240
```

Ctrl+C stops at a message boundary and each device sends a clean MQTT DISCONNECT.

---

## Options

| Option | Env | Default | What it does |
|---|---|---|---|
| `--hub <name\|hostname>` | `IOT_HUB` | — | The hub. A bare name gains `.azure-devices.net`; anything containing a dot is used verbatim, so a sovereign-cloud hostname works too. |
| `--device-id <id>` | `DEVICE_ID` | — | A registered device identity. |
| `--device-key <key>` | `DEVICE_KEY` | — | That device's base64 shared access key. |
| `--devices <path>` | `DEVICES_FILE` | — | A fleet file (format below). Mutually exclusive with the two options above in practice; if both are given, the file wins. |
| `--device-count <n>` | — | `1` | Simulate *n* devices concurrently, taking the first *n* entries of the fleet file. Above 1 requires `--devices`: one key authenticates one identity. |
| `--interval <seconds>` | — | `60` | Delay between messages, per device. |
| `--count <n>` | — | unbounded | Messages per device, then stop. |
| `--once` | — | — | Shorthand for `--count 1`. |
| `--fixture <selector>` | — | `valid` | `all`, `valid`, `invalid`, or a fixture name such as `valid-buffered-backfill` (the `.json` is optional). An unknown name is an error, never an empty run. |
| `--no-mutate` | — | — | Send fixture bytes verbatim. See below. |
| `--sas-lifetime <minutes>` | — | `60` | SAS token validity. |
| `--dry-run` | — | — | Build and print messages; connect to nothing. |
| `-h`, `--help` | — | — | Usage. |

### Fixture file

```json
[
  { "deviceId": "tank-000001", "key": "<base64 key>" },
  { "deviceId": "tank-000002", "key": "<base64 key>" }
]
```

Keep it outside the repository. It is a list of credentials.

---

## Mutation, and why `deviceId` is rewritten

By default each replayed fixture is edited so it is a fresh, acceptable message. Four fields
change:

| Field | New value | Why |
|---|---|---|
| `messageId` | unique per message | The contract uses it for idempotency on resend; reusing it invites a consumer to drop the message as a duplicate. |
| `sentAt` | now | It is the device's transmission time. |
| `measurements[].measuredAt` | shifted by one constant offset | Keeps the readings recent *and* keeps their relative spacing. |
| `deviceId` | the authenticated device id | See below. |

**`deviceId` is the one that is easy to miss.** Section 4 of the contract makes the payload's
`deviceId` non-authoritative: the Function cross-checks it against the IoT Hub system property
`iothub-connection-device-id` and rejects a mismatch as a *security event*. Both
`valid-full.json` and `valid-buffered-backfill.json` hard-code `"deviceId": "tank-000123"`, so
without this rewrite every message from a device registered under any other name is correctly
rejected — and the rejection looks like a broken pipeline rather than stale test data.

**One constant offset, not one timestamp per reading.** Every `measuredAt` moves by the same
amount, so the spacing between readings survives and so does their relationship to `sentAt`.
`valid-buffered-backfill.json` therefore still looks like genuine backfill: three readings four
hours apart, the newest still older than `sentAt`. Re-stamping each reading as "now" would flatten
exactly the shape that fixture exists to exercise. (Per the contract, a reading must *never* be
rejected for an old `measuredAt` — that is a device emptying its buffer after an outage.)

### `--no-mutate`

Sends the fixture bytes exactly as they appear on disk. Use it for contract and negative testing,
where the question is what the pipeline does with *the published bytes* rather than with a fresh
message. Expect the two fixtures carrying `tank-000123` to be rejected on the identity cross-check
unless that happens to be the device you are authenticating as — that rejection is the check
working.

Invalid fixtures are sendable in either mode. Proving that a bad message is rejected with a logged
reason is an acceptance criterion of
[#10](https://github.com/asnielsen789/IIoT-platform/issues/10), so the client never filters them
out.

---

## What is verified, and what is not

`tests/IIoT.TestClient.Tests` covers everything that can be checked without a hub: the exact topic
string, SAS generation against a known value computed outside this codebase, the mutation rules,
byte-for-byte fidelity in `--no-mutate` mode, fixture selection, and the CONNECT packet's contents.
One test runs every mutated `valid-*` fixture through `IIoT.Core`'s own `TelemetryValidator` — the
test project references `IIoT.Core` even though the client does not — which is what proves the
client and the Function agree about the contract rather than merely agreeing with each other.

**No test opens a network connection.** Nothing below has been verified against a live hub, and
none of it can be until [#9](https://github.com/asnielsen789/IIoT-platform/issues/9) provisions
one:

- that the documented topic string is accepted and routes on message content
- that the username, `api-version` and SAS token are accepted by a real hub
- TLS negotiation, keep-alive and reconnection behaviour
- that IoT Hub stamps `iothub-connection-device-id` as the trust model assumes
- throughput and throttling with a concurrent fleet

The first live run is [#10](https://github.com/asnielsen789/IIoT-platform/issues/10).
