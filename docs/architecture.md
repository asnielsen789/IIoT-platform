# Architecture — IoT Cloud Platform

## C4 Model

### Level 1: Context diagram

Shows the system as a whole and its relationships to external actors.

```mermaid
graph LR
    subgraph Actors
        sensor["NB-IoT Level Sensor<br/>(~3000 devices)"]
        operator["Operations Staff"]
        admin["System Administrator"]
    end

    platform["IoT Cloud Platform<br/>(Azure Tenant)<br/>---<br/>Receives sensor data,<br/>manages certificates,<br/>forwards downstream"]

    subgraph "External systems"
        gw["Downstream Gateway<br/>(legacy)"]
        gw_new["Downstream Gateway<br/>(future)"]
        auth0["Auth0<br/>(identity provider)"]
    end

    sensor -- "MQTT over TLS" --> platform
    platform -- "HTTPS + JWT" --> gw
    platform -. "HTTPS + JWT<br/>(future)" .-> gw_new
    platform -- "configuration<br/>(cloud-to-device)" --> sensor
    auth0 -- "JWT tokens" --> platform
    operator -- "dashboard/API" --> gw
    admin -- "Azure Portal /<br/>IaC deploys" --> platform
```

### Level 2: Container diagram

Shows the internal components of the Azure platform.

```mermaid
graph TB
    sensor["NB-IoT Sensor"] -- "MQTT / TLS<br/>X.509 cert" --> iothub
    sensor -. "first start:<br/>MQTT / TLS, X.509 cert" .-> dps

    subgraph azure ["Azure Tenant"]
        dps["Device Provisioning Service<br/>---<br/>Enrollment group for the device CA,<br/>assigns devices to hubs"]

        iothub["Azure IoT Hub<br/>---<br/>Device management,<br/>MQTT broker,<br/>message routing"]

        keyvault["Azure Key Vault<br/>---<br/>Device CA keys and certificates,<br/>secrets, connection strings"]

        function["Azure Function<br/>(.NET 10 / C#)<br/>---<br/>Message processing,<br/>transformation,<br/>downstream routing"]

        apim["Azure APIM<br/>(optional)<br/>---<br/>Rate limiting,<br/>API versioning,<br/>request logging"]

        monitor["Application Insights<br/>---<br/>Logging, metrics,<br/>alerting"]

        storage["Azure Storage<br/>---<br/>Host storage,<br/>dead-letter queues"]
    end

    iothub -- "Event trigger" --> function
    keyvault -. "issuing CA certificate<br/>(registered once by an operator)" .-> iothub
    keyvault -. "issuing CA certificate<br/>(registered once by an operator)" .-> dps
    dps -- "creates the device identity" --> iothub
    keyvault -- "secrets" --> function
    function --> apim
    function -- "telemetry" --> monitor
    function -- "failed forwarding,<br/>retried from the queue" --> storage
    iothub -- "telemetry" --> monitor

    auth0["Auth0"] -- "JWT token" --> function

    apim -- "HTTPS + JWT" --> gw["Downstream Gateway"]
    function -- "cloud-to-device" --> iothub

    classDef external fill:#f5f5f5,stroke:#999
    class sensor,auth0,gw external
```

### Component responsibilities

| Component | Responsibility | Modularity |
|-----------|----------------|------------|
| **IoT Hub** | Device registry, MQTT broker, message routing, C2D messages | Replaceable by another MQTT broker — no vendor lock-in in the application layer |
| **Device Provisioning Service** | Zero-touch provisioning: authenticates a device's certificate against the enrollment group for the device CA, creates its identity in the hub it assigns, and tells the device that hub ([#48](https://github.com/asnielsen789/IIoT-platform/issues/48)) | Hubs can be linked and unlinked, an operator's own included, so devices move between hubs without being changed; devices can also be registered without it (`IIoT.DeviceCa issue --hub`) |
| **Key Vault** | The device CA's signing keys, which never leave it, and its certificates ([ADR-009](adr/adr-009-device-pki.md)); secrets for the Function. Not a CA itself: `tools/IIoT.DeviceCa` builds the certificates and has Key Vault sign them | Plain keys and RFC 5280 certificates: the keys can move to an HSM, the issuing CA to another provider |
| **Azure Function** | Core logic: receive → transform → forward. Adapter pattern downstream | Can be relocated to other infrastructure as a container or service |
| **APIM** | Optional layer for throttling, versioning and logging of outbound calls | Can be removed without affecting the Function |
| **Azure Storage** | Host storage for the Function, and the dead-letter queues `forwarding-failed` and `forwarding-failed-poison` for messages that could not be forwarded ([ADR-010](adr/adr-010-dead-letter-handling.md)) | The queues have a connection of their own, so they can move to another account; any queue with retry and poison handling could replace them |
| **Application Insights** | Observability for the whole platform | Replaceable by another monitoring solution |

---

## Sequence: sensor data to downstream gateway

Normal flow for a level reading from sensor to the downstream system.

```mermaid
sequenceDiagram
    participant S as NB-IoT Sensor
    participant IH as Azure IoT Hub
    participant AF as Azure Function
    participant A0 as Auth0
    participant GW as Downstream Gateway

    Note over S,IH: Device authentication (on connect)
    S->>IH: TLS handshake with X.509 device certificate
    alt Registered by the signing tool
        IH->>IH: Verify chain to the registered issuing CA, CN = device ID
    else Provisioned through DPS
        IH->>IH: Match the certificate's thumbprint DPS registered
    end
    IH-->>S: Handshake complete, CONNECT accepted

    Note over S,GW: Data transmission (every 4 hours)
    S->>IH: MQTT PUBLISH (level reading)
    IH->>AF: Event trigger (message)
    AF->>AF: Transform to target format

    Note over AF,GW: Service-to-service auth
    AF->>A0: Request JWT token (client credentials)
    A0-->>AF: JWT token (cached, renewed before expiry)

    AF->>GW: HTTPS POST /v1/readings (batch + JWT + Idempotency-Key)
    alt 401 (token signed with a key the provider has rotated out)
        GW-->>AF: 401 Unauthorized
        AF->>A0: Request a new JWT
        A0-->>AF: New JWT token
        AF->>GW: Same POST, same Idempotency-Key (once)
    end
    GW-->>AF: 202 Accepted

    Note over AF: Log result to Application Insights
```

When the gateway stays unavailable through the forwarder's retries, the message is not lost
([ADR-010](adr/adr-010-dead-letter-handling.md)). `ProcessTelemetry` writes it to a Storage queue,
and `RetryForwarding` sends it again later, through the same validation and identity check, with
the identity and receipt time of the original delivery, and so with the same body and
`Idempotency-Key`:

```mermaid
sequenceDiagram
    participant AF as ProcessTelemetry
    participant Q as Queue forwarding-failed
    participant RF as RetryForwarding
    participant GW as Downstream Gateway
    participant P as Queue forwarding-failed-poison

    AF->>GW: HTTPS POST /v1/readings (with retries)
    GW-->>AF: 503, or no answer
    AF->>Q: Envelope: payload as received, authenticated device ID, enqueued time
    Note over Q: Invisible for 30 minutes
    loop Up to 8 attempts, 30 minutes apart
        Q->>RF: Entry
        RF->>GW: Same POST, same Idempotency-Key
        alt Delivered
            GW-->>RF: 202 Accepted (or a replay of a batch already stored)
            Note over Q: Entry deleted
        else Fails again
            GW-->>RF: 503
            Note over Q: Entry kept for the next attempt
        end
    end
    RF->>P: After the last attempt, or when re-validation rejects it
    Note over P: Kept without expiry, for an operator
```

## Sequence: zero-touch provisioning

A device's first start when nobody has registered it
([#48](https://github.com/asnielsen789/IIoT-platform/issues/48)). The device knows the global
endpoint, the ID scope and its certificate, and DPS tells it the hub. DPS registers the device by
its certificate's thumbprint, so a renewed certificate, or a move to another hub, is another
registration ([Devices provisioned through DPS](../infra/README.md#devices-provisioned-through-dps)).

```mermaid
sequenceDiagram
    participant S as NB-IoT Sensor
    participant DPS as Device Provisioning Service
    participant IH as Azure IoT Hub

    S->>DPS: TLS handshake with X.509 device certificate, CONNECT (registration ID = CN)
    DPS->>DPS: Verify chain to the enrollment group's issuing CA
    S->>DPS: SUBSCRIBE to the response topics
    S->>DPS: PUBLISH register {"registrationId": CN}
    DPS-->>S: 202 assigning, operationId, retry-after
    DPS->>IH: Create device identity (device ID = CN, certificate thumbprint)
    loop Until assigned, as often as retry-after says
        S->>DPS: PUBLISH operation status (operationId)
        DPS-->>S: 202 assigning, or 200 assigned (hub, device ID)
    end
    S->>IH: TLS handshake with the same certificate, then telemetry as above
```

## Sequence: cloud-to-device configuration

Sending a configuration message to a device. This is planned in
[#16](https://github.com/asnielsen789/IIoT-platform/issues/16) and not implemented yet.

```mermaid
sequenceDiagram
    participant Admin as Administrator
    participant AF as Azure Function
    participant IH as Azure IoT Hub
    participant S as NB-IoT Sensor

    Admin->>AF: HTTPS POST /devices/{id}/config
    AF->>AF: Validate configuration
    AF->>IH: Send C2D message (device twin / direct method)
    IH->>S: MQTT message (at next check-in)
    S-->>IH: ACK
    IH-->>AF: Delivery confirmation
    AF-->>Admin: 200 OK (configuration delivered)
```

---

## Design decisions

### Message format

Telemetry from sensor to IoT Hub follows a versioned JSON contract. The device transmits
raw measured values; conversion to volume is a cloud responsibility, so calibration can be
corrected without an OTA campaign across the fleet.

- Contract: [`mqtt-payload.md`](mqtt-payload.md)
- Normative schema: [`schemas/telemetry-v1.schema.json`](schemas/telemetry-v1.schema.json)
- Rationale: [ADR-004](adr/adr-004-message-format.md)

### Adapter pattern for downstream integration

So that migration from a current to a future downstream interface stays cheap:

```
Azure Function
    └── IDataForwarder (interface)
            ├── GatewayV1Forwarder       ← current gateway format
            └── GatewayV2Forwarder       ← future format (backlog, not implemented)
```

Today the Function registers `GatewayV1Forwarder` directly
(`src/IIoT.Functions/ForwardingServiceCollectionExtensions.cs`). Once a second forwarder
exists, the plan is to select it by configuration (an app setting) rather than a code change,
so both can run in parallel during a transition period.

The forwarder receives the whole validated message together with a forwarding context — the
authenticated device identity and IoT Hub's enqueued time — so identity and receipt time
downstream never come from the payload. The v1 format is the platform's own, versioned
independently of the device contract:

- Contract: [`gateway-contract.md`](gateway-contract.md)
- Normative schema: [`schemas/gateway/readings-v1.schema.json`](schemas/gateway/readings-v1.schema.json)
- Reference receiver: [`tools/IIoT.GatewayMock`](../tools/IIoT.GatewayMock/README.md)

### Modularity for hand-over

Components are designed so that an operator can take over parts of the platform:

```
          Can be relocated
                   │
    ┌──────────────┼──────────────┐
    ▼              ▼              ▼
 Function     APIM policies   Auth0 config
(container)   (export/import)  (tenant transfer)
    │
    └── IoT Hub remains in the platform tenant
        (or is migrated separately)
```

Devices that provisioned through DPS can be handed to an operator's own hub without being changed:
the operator's hub is linked to the platform's DPS, the enrollment is pointed at it, and each
device moves the next time it registers
([Moving devices to another hub](../infra/README.md#moving-devices-to-another-hub); not tested
yet).
