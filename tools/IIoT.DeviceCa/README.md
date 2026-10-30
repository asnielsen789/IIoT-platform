# IIoT.DeviceCa — the device CA's signing tool

A console tool for the platform's own device certificate authority
([ADR-009](../../docs/adr/adr-009-device-pki.md)): a self-signed root CA signs an issuing CA, and
the issuing CA signs device certificates, which devices use to authenticate to IoT Hub over
mutual TLS.

Both CA keys are EC P-256 keys in Key Vault, created by
[`infra/modules/device-ca-keys.bicep`](../../infra/modules/device-ca-keys.bicep) and not
exportable. Key Vault stores and uses keys but is not a certificate authority service, so this
tool does the CA's work: it builds each certificate with .NET's `CertificateRequest` and asks Key
Vault only to sign its SHA-256 digest (ES256). **The CA's private keys never leave the vault, and
a device's private key never reaches this tool**: the device sends a certificate signing request
(CSR), and only its public key is used.

The tool is run by an operator, never by the Function or CI. It signs in as you and refuses to
sign in as anything else ([Sign-in](#sign-in)).

> [!NOTE]
> The step-by-step procedure, with the roles an operator needs, is
> [Device certificate authority (one-time)](../../infra/README.md#device-certificate-authority-one-time)
> in the infrastructure README. This page describes the tool.

---

## Commands

Run from the repository root: `dotnet run --project tools/IIoT.DeviceCa -- <command> [options]`.
Every vault and hub is named on the command line, where you see it; the tool reads no
environment variable or configuration file for them.

### `init`: create the CA certificates

```
dotnet run --project tools/IIoT.DeviceCa -- init --vault <name|uri> [--force]
```

Builds the root and issuing CA certificates from the two keys and stores them in the vault.
Once per CA.

1. Reads the public keys of `device-ca-root` and `device-ca-issuing`, and checks that each is an
   EC P-256 key that may sign.
2. Builds the root CA certificate, `CN=IIoT Device Root CA`: self-signed, valid 20 years, a CA,
   key usage `keyCertSign` and `cRLSign`.
3. Builds the issuing CA certificate, `CN=IIoT Device Issuing CA`, signed by the root key: valid
   10 years (never beyond the root), a CA with path length 0, so it can sign devices but never
   another CA.
4. Verifies the chain as a relying party would, then stores the root and then the issuing
   certificate as the secrets `device-ca-root-certificate` and `device-ca-issuing-certificate`
   (PEM, content type `application/x-pem-file`).
5. Prints both subjects, their validity and their SHA-256 thumbprints.

Every certificate the tool makes starts five minutes in the past, so a device or a hub whose
clock runs slightly behind does not see it as not yet valid.

**`--force`** replaces existing CA certificates. Without it, `init` refuses if either secret
exists and changes nothing. Replacing them orphans every device issued so far: their chain files
hold the old issuing CA certificate, and IoT Hub has the old one registered. Use it only to
start the CA again, for example after the vault was purged and the keys were recreated; then
register the new issuing CA certificate and issue every device a new certificate.

### `ca-certificate`: export the issuing CA certificate

```
dotnet run --project tools/IIoT.DeviceCa -- ca-certificate --vault <name|uri> [--out <file>]
```

Writes the issuing CA certificate, the one IoT Hub authenticates devices against, as PEM: to
`<file>`, or to standard output with nothing else, so it can be redirected. With `--out` it also
prints the subject, the validity and the SHA-1 thumbprint, which is what IoT Hub shows for a
registered certificate. The certificate is parsed before it is written, so a damaged secret is
reported here rather than by IoT Hub. `--out` overwrites: the file is a copy of public data.

Register the file in IoT Hub as verified with the Azure CLI
([`az iot hub certificate create`](https://learn.microsoft.com/cli/azure/iot/hub/certificate#az-iot-hub-certificate-create)):

```
az iot hub certificate create --hub-name <hub> --name device-ca-issuing --path <file> --verified
```

`--verified` skips the proof-of-possession challenge. The tool does not answer that challenge
(it would mean signing a verification certificate with the issuing CA key); the operator who runs
both steps is the one proving ownership.

### `issue`: issue a device certificate and register the device

```
dotnet run --project tools/IIoT.DeviceCa -- issue --vault <name|uri> (--hub <name|hostname> | --no-register) \
  --csr <file> --device-id <id> [--out <dir>]
```

Issues a certificate for the key in a device's CSR and registers the device in IoT Hub, or, with
`--no-register`, leaves the registration to the Device Provisioning Service. The CSR
comes from the device; for the simulated device it is what `IIoT.TestClient create-csr` writes
([test client README](../IIoT.TestClient/README.md#certificate-authentication)).

The checks run cheapest and least invasive first, so a refusal leaves no file behind and no
device registered:

1. **The device ID** follows IoT Hub's rules: at most 128 characters, ASCII letters and digits
   and ``- . % _ * ? ! ( ) , : = @ $ '``
   ([device identity properties](https://learn.microsoft.com/azure/iot-hub/iot-hub-devguide-identity-registry#device-identity-properties)).
2. **The CSR** verifies: its signature proves the device holds the private key, and the key is EC
   P-256 on the named curve. RSA, other curves and explicit curve parameters are refused.
3. **The output files** `<id>.cert.pem` and `<id>.chain.pem` do not exist yet.
4. **The registry**: if the device exists with another authentication type, such as a shared
   access key, nothing is issued.
5. **The CA**: both CA certificates are in the vault and chain, the issuing certificate is valid
   now, and the issuing key is the one it certifies.

Then it signs and writes:

- **The certificate.** Subject `CN=<id>` whatever the CSR asked for (IoT Hub requires the CN to
  be the device ID, and nothing else in a CSR is trusted); valid two years, never beyond the
  issuing CA; not a CA; key usage `digitalSignature`; extended key usage client authentication
  only; a random 16-byte serial number. It is verified as IoT Hub will check it before anything
  is written.
- **`<id>.cert.pem`**, the device certificate, and **`<id>.chain.pem`**, the device certificate
  followed by the issuing CA certificate. The root is left out: IoT Hub has the issuing CA
  registered, and every byte in the handshake crosses NB-IoT on every connection. The test client
  sends the device certificate alone ([why](../IIoT.TestClient/README.md#what-goes-on-the-wire)).
  Neither file is ever overwritten.
- **The registration**, if the device is new: authentication type *CertificateAuthority*,
  enabled, with no key or thumbprint. IoT Hub authenticates it by its certificate chaining to the
  verified issuing CA. A device already registered as CA-authenticated is left as it is.

Characters that some file systems refuse, such as `:`, become `_` in the file names; the test
client names its key and CSR the same way.

**`--no-register`, for a device that provisions itself through DPS**
([#48](https://github.com/asnielsen789/IIoT-platform/issues/48)). The tool issues and writes as
above, but opens no hub and registers nothing. The enrollment group for the issuing CA creates the
identity when the device first connects to DPS
([Zero-touch provisioning (one-time)](../../infra/README.md#zero-touch-provisioning-one-time)).
DPS registers the device by its certificate's thumbprint, not as `certificateAuthority`, which
matters for renewal and revocation
([Devices provisioned through DPS](../../infra/README.md#devices-provisioned-through-dps)).
`--no-register` takes the place of `--hub`: one of the two is required, and both together are an
error, so leaving out `--hub` can never skip a registration by accident.

The device ID is then checked against DPS's rules as well, because the certificate's CN becomes the
registration ID and, from that, the device ID in the hub: ASCII letters and digits and `- . _ :`,
ending in a letter, a digit or `-`, at most 64 characters
([DPS terminology](https://learn.microsoft.com/azure/iot-dps/concepts-service#registration-id)).
`tank@000003`, a valid IoT Hub device ID, is refused there, since DPS could never provision it.

### Options

| Option | Commands | What it does |
|---|---|---|
| `--vault <name\|uri>` | all | The Key Vault holding the CA. A bare name gains `https://` and `.vault.azure.net`; a value with `://` is used as it is, for another cloud. |
| `--hub <name\|hostname>` | `issue` | The IoT hub to register the device in. A bare name gains `.azure-devices.net`. |
| `--no-register` | `issue` | Instead of `--hub`: register nothing, because the device provisions itself through DPS. The device ID must also be a valid DPS registration ID. |
| `--csr <file>` | `issue` | The device's CSR, PEM. |
| `--device-id <id>` | `issue` | The IoT Hub device ID, and the certificate's subject. |
| `--out <dir\|file>` | `issue`, `ca-certificate` | `issue`: the directory for the certificate and chain, created if missing; default the current directory. `ca-certificate`: the file; default standard output. |
| `--force` | `init` | Replace existing CA certificates. |
| `-h`, `--help` | — | Usage. |

An option a command does not use is an error, not something ignored.

---

## Sign-in

The tool uses your own Microsoft Entra sign-in, through `DefaultAzureCredential` with the
credentials a machine or a pipeline would bring excluded: environment variables, workload
identity and managed identity. What remains, in Azure.Identity 1.21's order, is Visual Studio,
Visual Studio Code, the Azure CLI, Azure PowerShell, the Azure Developer CLI and the
authentication broker. In practice that is `az login`.

- **A Visual Studio sign-in comes first.** If Visual Studio is signed in with another account,
  that account is used. Pin the Azure CLI with `export AZURE_TOKEN_CREDENTIALS=azureclicredential`.
- **`AZURE_TOKEN_CREDENTIALS` is checked.** Azure.Identity lets that variable name a single
  credential, and a credential named there overrides the exclusions above. The tool therefore
  refuses to run if it names anything other than a developer sign-in (`dev`,
  `azureclicredential`, `azurepowershellcredential`, `azuredeveloperclicredential`,
  `visualstudiocredential`, `visualstudiocodecredential`, `interactivebrowsercredential`,
  `brokercredential`).
- **No shared access key.** Key Vault is called with the sign-in's token, and IoT Hub through
  `RegistryManager.Create(hostName, TokenCredential)`, which also uses Microsoft Entra ID. No
  hub-level policy key is read.

The roles, all data-plane, each scoped as narrowly as the operation allows, are set up in
[Device certificate authority (one-time)](../../infra/README.md#device-certificate-authority-one-time):

| Operation | Permission | Built-in role |
|---|---|---|
| Sign with a CA key (`init`: both keys; `issue`: the issuing key) | `keys/read`, `keys/sign/action` | Key Vault Crypto User |
| Create the CA certificate secrets (`init`) | `secrets/getSecret/action`, `secrets/setSecret/action` | Key Vault Secrets Officer |
| Read them (`issue`, `ca-certificate`) | `secrets/getSecret/action` | Key Vault Secrets User |
| Look up and register the device (`issue`) | `IotHubs/devices/read`, `IotHubs/devices/write` | IoT Hub Registry Contributor |

A refusal from Key Vault (403) or IoT Hub (unauthorized) is reported with the role it needs. A
new role assignment can take a few minutes to apply.

---

## What it writes

| Where | What | Secret? |
|---|---|---|
| Key Vault secret `device-ca-root-certificate` | The root CA certificate, PEM (`init`) | No |
| Key Vault secret `device-ca-issuing-certificate` | The issuing CA certificate, PEM (`init`) | No |
| `<dir>/<id>.cert.pem` | The device certificate (`issue`) | No |
| `<dir>/<id>.chain.pem` | Device certificate, then the issuing CA certificate (`issue`) | No |
| `<file>` or standard output | The issuing CA certificate (`ca-certificate`) | No |
| IoT Hub identity registry | The device, CA-authenticated (`issue`) | No key or thumbprint is stored |

Nothing it writes is secret. The certificates are stored as Key Vault *secrets* only because a
secret is the vault's plain "store a document" primitive; a Key Vault *certificate* object would
bind each one to a key the vault generates for it, which is not how these keys are managed.

## What it refuses to do

- Replace existing CA certificates without `--force`.
- Overwrite a device's certificate or chain file.
- Issue for a device ID that IoT Hub would refuse, or for a CSR whose signature does not verify
  or whose key is not EC P-256.
- Take the subject from the CSR.
- Change how an existing device authenticates. Moving a device from a shared access key to a
  certificate is a decision about that device, not a side effect of issuing one: delete the
  identity first if that is really meant.
- Sign with a key that does not belong to its CA certificate, with a CA certificate that is not
  valid now, or with a Key Vault key that is not an EC P-256 signing key.
- Sign in as a service principal, a pipeline or a managed identity.

## What it does not do

- **Register the CA certificate in IoT Hub.** That is the one-time `az iot hub certificate
  create` above, a control-plane operation.
- **Revoke.** IoT Hub does not check certificate revocation lists
  ([Revoke a device certificate](https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-x509#revoke-a-device-certificate)).
  A compromised device registered with `--hub` is disabled in the identity registry:
  `az iot hub device-identity update --hub-name <hub> --device-id <id> --status disabled`. A
  compromised issuing CA is removed from IoT Hub, which stops every device it registered, and a
  new one is created (ADR-009). A device that provisioned through DPS is registered by its own
  thumbprint instead, so it must be shut out in DPS as well as in the hub
  ([Revoking a device](../../infra/README.md#revoking-a-device)).
- **Renew by itself.** `issue` makes a new certificate for the same CN, from the device's CSR,
  but getting the request from the device and the certificate back to it is
  [#16](https://github.com/asnielsen789/IIoT-platform/issues/16). For a device registered with
  `--hub`, the new certificate works at once, since the hub trusts the CA. A DPS device must
  provision again with it, and `--hub` is refused for it because its identity is not
  `certificateAuthority`; renew it with `--no-register`
  ([Renewing a device certificate](../../infra/README.md#renewing-a-device-certificate)).
- **Provision through DPS.** `--no-register` leaves that to the device: the test client's
  [`provision`](../IIoT.TestClient/README.md#zero-touch-provisioning).
- **Turn off shared access keys** for devices. That belongs to the security review,
  [#21](https://github.com/asnielsen789/IIoT-platform/issues/21).

## Tests

`tests/IIoT.DeviceCa.Tests` runs offline: the certificate profiles, the conversion of Key Vault's
signatures to the form X.509 needs, CSR validation, and the commands' rules against a fake vault
and a fake registry, with local keys standing in for the Key Vault ones. Nothing reaches Azure.
`tests/IIoT.TestClient.Tests` issues certificates with this tool's code for keys the test client
generated and uses them in a loopback TLS handshake.
