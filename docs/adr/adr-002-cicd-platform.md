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

## Update (2026-10-19): OIDC and code-only deployment

Implementing continuous deployment in [#5](https://github.com/asnielsen789/IIoT-platform/issues/5) changed how the pipeline authenticates to Azure, and settled what it deploys. The choice of GitHub Actions stands.

- **Workload identity federation (OIDC) instead of a service principal with a secret.** The deploy workflow (`.github/workflows/deploy.yml`) authenticates as a user-assigned managed identity that the Bicep templates create for it alone. A federated identity credential on that identity trusts GitHub's OIDC tokens for one subject: this repository, named by its immutable owner and repository IDs, in the GitHub environment `staging`. Each run receives a short-lived token from GitHub, and Microsoft Entra ID exchanges it for an Azure access token ([workload identity federation](https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation)). No long-lived credential exists to store in GitHub, leak or rotate, and a token is of use only to a job of this repository running in that environment, which admits only `main`. A managed identity rather than an app registration, because it is an ordinary Azure resource: created, scoped and deleted with the rest of the environment, without rights in Entra ID. It is separate from the identity the Function App runs as, and its one role is Website Contributor on the Function App, as in [Microsoft's guide for GitHub Actions and Azure Functions](https://learn.microsoft.com/en-us/azure/azure-functions/functions-how-to-github-actions). The rationale above that relies on a service principal (an app registration in Entra ID) no longer describes the implementation.
- **The Function's code deploys automatically on merge to `main`.** Each change to `main` that alters the deployed Function is built, tested and packaged in one run, deployed, and checked: the Key Vault references must resolve and the function must be listed. GitHub records every deployment, with its commit, under the `staging` environment.
- **Infrastructure stays manual.** The templates create role assignments, so deploying them requires the right to assign roles on the subscription (Owner, or Contributor together with Role Based Access Control Administrator): close to full control of the subscription. Giving that to an identity that any merged workflow change can use would make it the most powerful credential in the project, for changes that are rare and are reviewed with what-if before they are applied (`infra/README.md`).
- **What-if on pull requests is deferred** for the same reason. It would need a second Azure identity with rights across the subscription, usable from pull request context, where the workflow runs code that has not yet been reviewed.
