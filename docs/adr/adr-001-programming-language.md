# ADR-001: Choice of programming language

## Status

Accepted

## Context

The project builds an Azure-based IoT platform using Azure Functions, IoT Hub and Key Vault. The team is a single developer. The language needs good Azure SDK support and must suit a serverless architecture.

## Options

1. **C# / .NET 8+** — Azure-native, strong IoT SDK, type safety, first-class Azure Functions support.
2. **Python** — Fast to prototype, good IoT community, but weaker type safety and Azure Functions performance.
3. **Node.js/TypeScript** — Good async model, Azure support, but a less mature IoT SDK.

## Decision

C# / .NET 8+.

## Rationale

- Azure Functions is built on .NET — first-class support and the best performance.
- The Azure IoT Hub SDK for .NET is the most mature and best documented.
- Type safety is valuable in a security-critical IoT pipeline.
- Existing team competence in C#.

## Consequences

- Tighter coupling to the Microsoft ecosystem, which is acceptable given the platform targets Azure.
- Slower to start than Python for simple prototypes.
- Good fit for long-term maintenance.
