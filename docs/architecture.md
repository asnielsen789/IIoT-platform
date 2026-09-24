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

    subgraph azure ["Azure Tenant"]
        iothub["Azure IoT Hub<br/>---<br/>Device management,<br/>MQTT broker,<br/>message routing"]

        keyvault["Azure Key Vault<br/>---<br/>X.509 certificates (CA),<br/>secrets, connection strings"]

        function["Azure Function<br/>(.NET 10 / C#)<br/>---<br/>Message processing,<br/>transformation,<br/>downstream routing"]

        apim["Azure APIM<br/>(optional)<br/>---<br/>Rate limiting,<br/>API versioning,<br/>request logging"]

        monitor["Application Insights<br/>---<br/>Logging, metrics,<br/>alerting"]

        storage["Azure Storage<br/>(optional)<br/>---<br/>Dead-letter queue,<br/>audit log"]
    end

    iothub -- "Event trigger" --> function
    keyvault -- "certificates" --> iothub
    keyvault -- "secrets" --> function
    function --> apim
    function -- "telemetry" --> monitor
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
| **Key Vault** | CA for X.509 device certificates, secrets for the Function | Standard certificate handling, can be migrated |
| **Azure Function** | Core logic: receive → transform → forward. Adapter pattern downstream | Can be relocated to other infrastructure as a container or service |
| **APIM** | Optional layer for throttling, versioning and logging of outbound calls | Can be removed without affecting the Function |
| **Application Insights** | Observability for the whole platform | Replaceable by another monitoring solution |

---

## Sequence: sensor data to downstream gateway

Normal flow for a level reading from sensor to the downstream system.

```mermaid
sequenceDiagram
    participant S as NB-IoT Sensor
    participant IH as Azure IoT Hub
    participant KV as Key Vault
    participant AF as Azure Function
    participant A0 as Auth0
    participant GW as Downstream Gateway

    Note over S,IH: Device authentication (on connect)
    S->>IH: TLS handshake with X.509 cert
    IH->>KV: Validate device certificate
    KV-->>IH: Certificate valid

    Note over S,GW: Data transmission (every 4 hours)
    S->>IH: MQTT PUBLISH (level reading)
    IH->>AF: Event trigger (message)
    AF->>AF: Transform to target format

    Note over AF,GW: Service-to-service auth
    AF->>A0: Request JWT token (client credentials)
    A0-->>AF: JWT token (1h lifetime, cached)

    AF->>GW: HTTPS POST (data + JWT)
    GW-->>AF: 200 OK / ACK

    Note over AF: Log result to Application Insights
```

## Sequence: cloud-to-device configuration

Sending a configuration message to a device.

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
            └── GatewayV2Forwarder       ← future format (stub)
```

Forwarder selection is driven by configuration (environment variable / app setting) rather
than a code change. Both implementations can run in parallel during a transition period.

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
