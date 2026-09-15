# ADR-002: Choice of CI/CD platform

## Status

Accepted

## Context

The project needs a CI/CD pipeline for building, testing and deploying Azure resources. The code is hosted on GitHub.

## Options

1. **GitHub Actions** — Integrated with GitHub, simple YAML configuration, Azure integration via service principal.
2. **Azure DevOps Pipelines** — Native Azure integration and a full project management suite, but a separate platform.

## Decision

GitHub Actions.

## Rationale

- The code already lives on GitHub, so this avoids splitting the project across two platforms.
- Simpler setup: `.github/workflows/` YAML in the repository itself.
- Azure integration via the `azure/login` action and a service principal is sufficient at this scale.
- Azure DevOps makes most sense for organisations already using the full Azure ecosystem for project management, which is outside this project's scope.
- Fewer dependencies: a single service principal (app registration in Entra ID).

## Consequences

- Less native Azure integration than Azure DevOps, acceptable at this scale.
- Everything lives in one repository, which is simpler to maintain.
- GitHub Projects is used for the Kanban board, so no separate project management tool is needed.
