# IIoT Platform

A reference architecture for secure, scalable industrial IoT telemetry on Azure.

Battery-powered NB-IoT devices publish readings over MQTT/TLS to Azure IoT Hub, where an
Azure Function validates them against a versioned contract and forwards them downstream
through a swappable adapter. Tank level monitoring is the worked example; the platform
itself is not specific to it.

The design is built around CRA (Regulation (EU) 2024/2847) and NIS2 compliance from the
message schema upward, and avoids vendor lock-in where open standards exist.

## Documentation

| Document | Contents |
|---|---|
| [Architecture](docs/architecture.md) | C4 diagrams, component responsibilities, sequence flows |
| [MQTT payload contract](docs/mqtt-payload.md) | Message format, trust model, CRA mapping, evolution rules |
| [Roadmap](docs/roadmap.md) | Iterations, dependencies, risks |
| [Architecture decisions](docs/adr/README.md) | ADRs with context, options and rationale |

## Status

Under active development. See the [roadmap](docs/roadmap.md) for current state.
