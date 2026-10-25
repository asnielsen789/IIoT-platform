# IIoT.TestClient — simulated NB-IoT device

A console application that stands in for device firmware. It publishes the contract's example
payloads to Azure IoT Hub over **raw MQTT/TLS**, so the platform can be exercised end to end
before any hardware exists.

> **Never commit a devices file, a device key, a device's private key or a connection string.**
> Everything secret is supplied at runtime — command line or environment — and this tool reads
> no configuration file other than the ones you point `--devices`, `--cert` and `--key` at. There
> is no `appsettings.json`, no user-secrets id and no gitignored-but-expected file. Keep your
> devices file and your device private keys outside the repository; `create-csr` writes keys to
> `~/.secrets/iiot` by default.

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

That is a device with a shared access key, the quick way to exercise a hub. The platform's
intended mechanism is an X.509 client certificate from the device CA
([ADR-009](../../docs/adr/adr-009-device-pki.md),
[#11](https://github.com/asnielsen789/IIoT-platform/issues/11)): see
[Certificate authentication](#certificate-authentication). Both go through the same
authentication seam (`IDeviceAuthentication`), so the connection and publishing code is the same
for both.

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

## Certificate authentication

The device's side of [ADR-009](../../docs/adr/adr-009-device-pki.md): the device makes its own
key pair and a certificate signing request (CSR), the device CA issues a certificate for it, and
the device connects with that certificate over mutual TLS. The private key never leaves the
device, which here means it never leaves the directory `create-csr` writes it to. The whole
procedure, including the CA's side and the roles it needs, is
[Device certificate authority (one-time)](../../infra/README.md#device-certificate-authority-one-time)
in the infrastructure README; this is the client's part of it.

1. **Make a key and a CSR**, as device firmware would on its first start. This connects to
   nothing:

   ```
   dotnet run --project tools/IIoT.TestClient -- create-csr --device-id tank-000002
   ```

   It writes `~/.secrets/iiot/tank-000002.key.pem`, a new EC P-256 private key (unencrypted
   PKCS#8), and `~/.secrets/iiot/tank-000002.csr.pem`, the request, which asks for
   `CN=tank-000002`. On Linux and macOS the key is created readable by you only, in a directory
   only you can open. It never overwrites a file: a device keeps its key. `--out <dir>` writes
   elsewhere, but not into the repository: `*.key.pem` is gitignored as a guard, not as a place
   to keep keys.

2. **Have the CA issue the certificate**, which also registers the device in IoT Hub
   ([`IIoT.DeviceCa`](../IIoT.DeviceCa/README.md)):

   ```
   dotnet run --project tools/IIoT.DeviceCa -- issue --vault <vault> --hub <hub> \
     --csr ~/.secrets/iiot/tank-000002.csr.pem --device-id tank-000002 --out ~/.secrets/iiot
   ```

3. **Connect with it.** Check the files first with `--dry-run`, which loads and checks them and
   prints the certificate's subject, issuer, expiry and thumbprint without connecting:

   ```
   dotnet run --project tools/IIoT.TestClient -- --hub <hub> --device-id tank-000002 \
     --cert ~/.secrets/iiot/tank-000002.chain.pem --key ~/.secrets/iiot/tank-000002.key.pem \
     --once --dry-run
   ```

   Then the same without `--dry-run`. Every other option works as with a key: `--fixture`,
   `--no-mutate`, `--interval`, `--count`.

### What the client checks before connecting

A certificate problem otherwise shows up only as a refused TLS handshake or CONNECT, with no
reason given, so the client checks first and says what is wrong:

- **The certificate's CN is exactly the device ID**, case included. IoT Hub authenticates a
  CA-signed device only if its certificate's subject is `CN=<deviceId>`
  ([Authenticate with X.509 certificates](https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-x509#sign-devices-into-the-certificate-chain-of-trust)).
- **The key belongs to the certificate**, and is an unencrypted PEM key.
- **The certificate is valid now.**
- **The options fit together.** `--cert` and `--key` go together, and not with `--device-key`,
  `--devices`, a `--device-count` above 1, or `--sas-lifetime`. With `--cert`, `DEVICE_KEY` and
  `DEVICES_FILE` in the environment are ignored, so a shell left over from a key session does not
  get in the way; `IOT_HUB` and `DEVICE_ID` still apply.

### What goes on the wire

- **The device certificate alone, not the chain.** `--cert` takes the `.chain.pem` that `issue`
  writes (device certificate, then the issuing CA) or the `.cert.pem`; either way only the first
  certificate is sent. MQTTnet 5.2.0.1603 gives .NET's TLS a plain certificate collection
  (`SslClientAuthenticationOptions.ClientCertificates`, set in `MqttTcpChannel`) and never an
  `SslStreamCertificateContext`, the .NET type that carries intermediates; its stream-provider
  extension point is documented as not doing TLS, so there is no other place to put one. On
  Windows, building that context with intermediates would also write them into your certificate
  store (`CurrentUser\CA`). Nothing is lost: IoT Hub has the issuing CA itself registered, which
  is the device certificate's direct issuer, so the device certificate alone is a complete path
  to it, and every handshake over NB-IoT is one certificate smaller. Microsoft's walkthrough does
  the same: it registers the subordinate CA that signs the devices
  ([Create and upload certificates for testing](https://learn.microsoft.com/azure/iot-hub/tutorial-x509-test-certs)).
  The live test of [#11](https://github.com/asnielsen789/IIoT-platform/issues/11) confirmed that
  the hub accepts it (2026-10-25).
- **The certificate is offered whatever the server asks for.** .NET offers a client certificate
  from a collection only if its chain matches one of the CA names the server lists, and
  otherwise continues without one. IoT Hub does not document its list, so the client also sets a
  selection handler that always returns the device certificate.
- **No password.** The CONNECT has the same client ID and username as with a key, and a
  zero-length password: IoT Hub needs no SAS token from a certificate device
  ([Use MQTT to communicate with IoT Hub](https://learn.microsoft.com/azure/iot-hub/iot-mqtt-connect-to-iot-hub#use-the-mqtt-protocol-directly-from-a-device)).

### On Windows: a temporary copy of the key

Windows' TLS stack, SChannel, refuses a client certificate whose private key was loaded straight
from PEM, which gives an ephemeral key; the handshake fails with "The credentials supplied to the
package were not recognized". The client therefore re-imports the certificate and key through
PKCS#12 after loading them. On Windows that puts the key in a temporary key container, a file in
`%APPDATA%\Microsoft\Crypto\Keys`, for as long as the client runs; it is deleted when the run
ends, Ctrl+C included. A process that is killed rather than stopped can leave that file behind.
On Linux the round trip stays in memory.

### Not with a fleet

Certificate authentication is for one device per client. `--cert` with `--devices` or a
`--device-count` above 1 is rejected: a fleet would need a certificate and a key per device in
the devices file, a format of its own, and nothing in #11 needs it. Run one client per device.

---

## Options

| Option | Env | Default | What it does |
|---|---|---|---|
| `--hub <name\|hostname>` | `IOT_HUB` | — | The hub. A bare name gains `.azure-devices.net`; anything containing a dot is used verbatim, so a sovereign-cloud hostname works too. |
| `--device-id <id>` | `DEVICE_ID` | — | A registered device identity. |
| `--device-key <key>` | `DEVICE_KEY` | — | That device's base64 shared access key. |
| `--cert <file>` | — | — | The device's X.509 certificate, PEM, device certificate first: the `.chain.pem` or `.cert.pem` from `IIoT.DeviceCa issue`. Its CN must be the device ID. Selects [certificate authentication](#certificate-authentication); then `DEVICE_KEY` and `DEVICES_FILE` are ignored. |
| `--key <file>` | — | — | The private key `create-csr` wrote for that certificate. Required with `--cert`. |
| `--devices <path>` | `DEVICES_FILE` | — | A fleet file (format below). Mutually exclusive with the two options above in practice; if both are given, the file wins. |
| `--device-count <n>` | — | `1` | Simulate *n* devices concurrently, taking the first *n* entries of the fleet file. Above 1 requires `--devices`: one key authenticates one identity. |
| `--interval <seconds>` | — | `60` | Delay between messages, per device. |
| `--count <n>` | — | unbounded | Messages per device, then stop. |
| `--once` | — | — | Shorthand for `--count 1`. |
| `--fixture <selector>` | — | `valid` | `all`, `valid`, `invalid`, or a fixture name such as `valid-buffered-backfill` (the `.json` is optional). An unknown name is an error, never an empty run. |
| `--no-mutate` | — | — | Send fixture bytes verbatim. See below. |
| `--sas-lifetime <minutes>` | — | `60` | SAS token validity. Not with `--cert`. |
| `--dry-run` | — | — | Build and print messages; connect to nothing. With `--cert`, also loads and checks the certificate and key. |
| `-h`, `--help` | — | — | Usage. |

`create-csr` takes two options of its own, and no others:

| Option | Env | Default | What it does |
|---|---|---|---|
| `--device-id <id>` | `DEVICE_ID` | — | The device the new key is for; the CSR asks for `CN=<id>`. |
| `--out <dir>` | — | `~/.secrets/iiot` | Where `<id>.key.pem` and `<id>.csr.pem` are written. Created if missing. |

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

For certificates, the tests generate a key and CSR with the client, check the CSR with the device
CA's own `DeviceCertificateRequest`, and issue the certificate with the CA's own
`CertificateAuthority`; the test project references `IIoT.DeviceCa` for that, as it references
`IIoT.Core`. One test then runs a real TLS handshake on 127.0.0.1, TLS 1.2 and TLS 1.3, through
`DeviceConnection` and MQTTnet against a local TLS server that requires a client certificate,
and asserts that the device certificate is presented alone, verifies against the CA, and is
followed by a CONNECT with the usual username and an empty password. Everything is generated at
runtime; no key or certificate is committed.

**No test reaches beyond loopback.** What only a live hub can show was checked against staging
instead:

- **With a key** ([#10](https://github.com/asnielsen789/IIoT-platform/issues/10), 2026-10-15):
  - the documented topic string is accepted and routes on message content;
  - the username, `api-version` and SAS token are accepted;
  - IoT Hub stamps `iothub-connection-device-id` as the trust model assumes.
- **With a certificate** ([#11](https://github.com/asnielsen789/IIoT-platform/issues/11),
  2026-10-25):
  - IoT Hub accepts the device certificate sent without the issuing CA it has registered, and
    the zero-length password in the CONNECT;
  - the message takes the route and is processed;
  - a disabled device is refused with `NotAuthorized`.
- **Not yet verified:**
  - keep-alive and reconnection behaviour;
  - throughput and throttling with a concurrent fleet, which is the scale test
    ([#20](https://github.com/asnielsen789/IIoT-platform/issues/20)).
