# ADR-009: Device identity — X.509 certificates from an own CA

## Status

Accepted

## Context

Devices authenticate to IoT Hub with a shared access key so far: the test client (#8) signs a
SAS token with a per-device symmetric key that IoT Hub also holds. The platform's intended
mechanism has always been X.509 client certificates over mutual TLS (#11), for three reasons:

- **No device secret in the cloud.** With a certificate, the device proves possession of a
  private key that nothing else holds. With SAS, IoT Hub stores the same key the device signs
  with.
- **One trust anchor for a fleet.** IoT Hub's "X.509 CA signed" mode authenticates any device
  whose certificate chains to a CA certificate registered once, with proof of possession.
  Microsoft recommends it for production. The device certificate's subject must be
  `CN=<deviceId>`.
- **The CRA.** Annex I, Part I, point (d) requires protection from unauthorised access through
  appropriate control mechanisms such as authentication and identity management. A
  per-device asymmetric key is the strongest of the mechanisms IoT Hub offers.

Facts that constrain the design:

- IoT Hub **does not check certificate revocation lists**. A compromised device is blocked by
  disabling it in the identity registry.
- Key Vault stores and uses keys, but it is **not a certificate authority service**. It can
  self-sign a certificate or merge one issued elsewhere; it cannot act as a CA that signs
  other certificates' requests.
- Microsoft offers a managed PKI as part of Azure Device Registry ("certificate
  management"). Since November 2025 it has been in **public preview, not recommended for
  production**. It requires Azure Device Registry and the Device Provisioning Service (DPS),
  and issues leaf certificates valid for 7 to 90 days.
- Devices are battery-powered NB-IoT units that connect for each transmission, so the full
  certificate chain is sent on every TLS handshake. Whether real NB-IoT modems handle mutual
  TLS with client certificates well is unverified (#22).
- DPS accepts RSA and ECC certificates and lists P-256 among its recommended curves. It costs
  0.10 USD per 1,000 operations in North Europe.

## Options

**PKI:**

1. **An own CA with its keys in Key Vault.** Root and issuing CA keys are generated in Key Vault
   and never leave it. A small signing tool builds the certificates and asks Key Vault only to
   sign.
2. **Microsoft-managed PKI** (Azure Device Registry certificate management). No CA to run, but
   preview, tied to Azure, and short-lived certificates mean frequent renewals on battery
   devices.
3. **Self-signed device certificates, registered by thumbprint.** No CA at all, but every device
   is registered individually and the fleet has no common trust anchor.
4. **A purchased CA certificate.** Microsoft's advice where devices interact with third parties.
   These devices talk only to the operator's IoT hub, where Microsoft accepts a self-signed CA
   for production.

**Registration in IoT Hub:**

1. **By the signing tool,** when it issues the certificate.
2. **By DPS,** on the device's first connection, through an enrollment group for the CA.

**Key algorithm:** ECDSA P-256 or RSA 2048.

## Decision

**An own CA in Key Vault, ECDSA P-256, keys generated on the device, and registration staged
from the signing tool to DPS.**

- **Hierarchy.** A self-signed root CA signs an issuing CA; the issuing CA signs device
  certificates. Both keys are EC P-256 keys generated in Key Vault and not exportable. The
  issuing CA certificate is registered in IoT Hub as verified.
- **Signing tool.** A command-line tool in `tools/` builds the CA and device certificates. .NET's
  `CertificateRequest` accepts a custom signature generator, so the tool hands Key Vault only
  the data to sign. It is run by an operator, never by the Function or CI.
- **Device keys never leave the device.** The device generates its key pair and sends a
  certificate signing request (CSR). The tool verifies the CSR's signature and sets the subject
  itself (`CN=<deviceId>`) rather than trusting the one in the request. The test client works the
  same way, with its key stored outside the repository.
- **Device certificates** are valid for about two years, for client authentication only.
- **Registration is staged.** The tool registers each device in IoT Hub as CA-authenticated when
  it issues the certificate. That is enough for the end-to-end test (#15). Zero-touch
  provisioning through DPS is #48.
- **Revocation** is disabling the device in IoT Hub. If the issuing CA is compromised, its
  certificate is removed from IoT Hub, which stops every device it signed, and a new issuing CA
  is created.
- **SAS stays enabled** until every device uses a certificate. Turning it off (`disableDeviceSAS`)
  belongs to the security review (#21).
- Microsoft-managed PKI is not ruled out for good. It is worth revisiting once it is generally
  available, if its certificate lifetimes suit battery devices.

## Consequences

- **No device secret exists in Azure.** IoT Hub holds only the issuing CA's public
  certificate, and one registration authenticates any number of devices.
- **Provider-neutral.** The certificates are plain RFC 5280 X.509. Moving the CA keys to an HSM,
  or the issuing CA to a commercial provider, replaces the signer, not the devices' trust
  model.
- **The project maintains a small signing tool.** Its certificate profile (extensions, key
  usage, validity) must be tested, because a malformed certificate fails only at connection
  time.
- **Key protection in staging is software-based.** Key Vault Standard protects keys in software.
  Production should use HSM-backed keys (Key Vault Premium or Managed HSM) and keep the root
  offline. That is documented, not built.
- **Renewal is not solved here.** A device lives 10+ years and its certificate about two.
  Renewal will come through C2D (#16) or re-provisioning through DPS (#48).
- **Revocation is coarse.** It works per device and per issuing CA, not by revocation list,
  because IoT Hub does not check one.
- **An operator who issues certificates needs data-plane roles** on the vault (to sign) and the
  hub (to register devices). The implementation documents them.
- **The hardware risk remains (#22).** If an NB-IoT modem cannot do mutual TLS with a client
  certificate, device authentication needs redesigning.

## References

- [Authenticate with X.509 certificates — Azure IoT Hub](https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-x509)
- [X.509 certificate attestation — Azure IoT Hub Device Provisioning Service](https://learn.microsoft.com/azure/iot-dps/concepts-x509-attestation)
- [What's new in Azure IoT Hub](https://learn.microsoft.com/azure/iot-hub/iot-hub-what-is-new) — Azure Device Registry certificate management (preview)
- `docs/mqtt-payload.md`, section 5 — the CRA mapping this decision adds to
- Cooper, D., et al. (2008). *Internet X.509 Public Key Infrastructure Certificate and
  Certificate Revocation List (CRL) Profile* (RFC 5280). IETF
- Issues: [#11](https://github.com/asnielsen789/IIoT-platform/issues/11), [#15](https://github.com/asnielsen789/IIoT-platform/issues/15), [#16](https://github.com/asnielsen789/IIoT-platform/issues/16), [#21](https://github.com/asnielsen789/IIoT-platform/issues/21), [#22](https://github.com/asnielsen789/IIoT-platform/issues/22), [#48](https://github.com/asnielsen789/IIoT-platform/issues/48)
