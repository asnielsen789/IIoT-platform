# Infrastructure

Bicep templates for the platform's Azure resources: IoT Hub, Key Vault, the Function App on a
Flex Consumption plan with its storage account, and monitoring.

> [!WARNING]
> **Nothing here has been deployed, and nothing should be yet.** This work
> ([#2](https://github.com/asnielsen789/IIoT-platform/issues/2)) is complete at a clean
> `what-if`, which creates nothing and costs nothing. The first real deployment belongs to
> [#9](https://github.com/asnielsen789/IIoT-platform/issues/9), and it must be preceded by a
> budget with alerts on the subscription. A pay-as-you-go subscription has no spending limit,
> so the budget alert is the only early warning of an unexpected bill. Do not run
> `az deployment sub create` before it exists.

## Layout

| File | Contents |
|---|---|
| `main.bicep` | Entry point, subscription scope. Creates the resource group, fixes every resource name, deploys the modules |
| `environments/staging.bicepparam` | The `staging` environment, the only one in Azure. `dev` is local development |
| `modules/identity.bicep` | User-assigned managed identity the Function App runs as |
| `modules/monitoring.bicep` | Log Analytics workspace and workspace-based Application Insights |
| `modules/storage.bicep` | Host storage and deployment package container, with the identity's storage roles |
| `modules/key-vault.bicep` | Key Vault (Azure RBAC), with the identity's read access to secrets |
| `modules/iot-hub.bicep` | IoT Hub and its shared access policies |
| `modules/iot-hub-connection-secret.bicep` | Builds the Function's IoT Hub connection string and writes it to Key Vault |
| `modules/function-app.bicep` | Flex Consumption plan and Function App |

## Prerequisites

- Azure CLI with Bicep **0.48 or later** (`az bicep upgrade`). Several API versions used here
  are recent; older Bicep releases, including older releases of the VS Code extension, have no
  type information for them and report warning BCP081.
- Signed in to the target subscription: `az account show`.
- To deploy: Owner on the subscription, or Contributor plus Role Based Access Control
  Administrator, because the template creates role assignments.
- No other free (F1) IoT hub in the subscription. Azure allows one.
- The resource providers `Microsoft.Devices`, `Microsoft.Web`, `Microsoft.KeyVault`,
  `Microsoft.Storage`, `Microsoft.Insights`, `Microsoft.OperationalInsights` and
  `Microsoft.ManagedIdentity` registered on the subscription
  (`az provider show --namespace <namespace> --query registrationState`).

## Build and preview

From the repository root. These commands change nothing in Azure.

```sh
# Compile and lint, offline
az bicep build --file infra/main.bicep
az bicep lint --file infra/main.bicep
az bicep build-params --file infra/environments/staging.bicepparam

# Preview the whole plan against the subscription
az deployment sub what-if \
  --location westeurope \
  --template-file infra/main.bicep \
  --parameters infra/environments/staging.bicepparam
```

`--location` is where Azure stores the deployment record; the resources take their region from
the parameter file. Treat what-if output as internal: do not paste it into issues, pull
requests or commits.

## Naming

Names follow the Cloud Adoption Framework abbreviations:
`<abbreviation>-<workload>-<environment>-<region>`, with workload `iiot` and region code `weu`
for West Europe. The four resources whose names are global DNS names also get a four-character
suffix, written `xxxx` below. It comes from `uniqueString()` over the subscription ID and the
environment name, so it is stable across redeployments and differs between subscriptions.

| Resource | Name in `staging` |
|---|---|
| Resource group | `rg-iiot-staging-weu` |
| Managed identity | `id-iiot-staging-weu` |
| Log Analytics workspace | `log-iiot-staging-weu` |
| Application Insights | `appi-iiot-staging-weu` |
| Storage account | `stiiotstagingweuxxxx` (storage names allow no hyphens) |
| Key Vault | `kv-iiot-staging-weu-xxxx` |
| IoT Hub | `iot-iiot-staging-weu-xxxx` |
| Flex Consumption plan | `asp-iiot-staging-weu` |
| Function App | `func-iiot-staging-weu-xxxx` |

Key Vault names are limited to 24 characters, which is why the environment name is limited to
seven. Every resource carries the tags `workload`, `environment` and `managed-by: bicep`.

## Design notes

### Hosting: Flex Consumption

.NET 10 does not run on the Linux Consumption plan, so the Function App is on Flex Consumption
([ADR-006](../docs/adr/adr-006-target-net10.md)). The templates follow Microsoft's
[Flex Consumption samples](https://github.com/Azure-Samples/azure-functions-flex-consumption-samples):
runtime (`dotnet-isolated` 10.0), scale and deployment storage are set in `functionAppConfig`,
and the classic app settings Flex deprecates are absent.

- **At most 10 instances.** A cap on runaway cost. At 2048 MB each instance takes one core, so
  the app stays inside the 15-core regional Flex Consumption quota of a free trial
  subscription.
- **No always-ready instances.** They bill continuously whether or not anything runs.

### Identity and access

The Function App runs as a **user-assigned** managed identity. Unlike a system-assigned one, it
exists before the app, so every role assignment below is in place before the app first starts.
The app is the last thing the template creates.

| Role | Scope | Needed for |
|---|---|---|
| Storage Blob Data Owner | Storage account | Host storage, the deployment package, Event Hubs trigger checkpoints |
| Storage Table Data Contributor | Storage account | Diagnostic events the host writes when the app cannot start |
| Key Vault Secrets User | Key Vault | Resolving the `IoTHubConnection` Key Vault reference |

Host storage is identity-based (`AzureWebJobsStorage__accountName`, `__credential`,
`__clientId`) and shared-key access to the storage account is disabled: no account key is used
anywhere.

### How the Function reads telemetry

Decided in [ADR-007](../docs/adr/adr-007-telemetry-stream-access.md): the IoT Hub trigger does
not support identity-based connections, so the Function reads the built-in endpoint with a
connection string held in Key Vault.

- The IoT Hub has a dedicated shared access policy, `function-telemetry-reader`, with only the
  `ServiceConnect` right. Declaring policies replaces the hub's default list, so the five
  defaults are restated next to it; trimming them is left to the
  [security review (#21)](https://github.com/asnielsen789/IIoT-platform/issues/21).
- The template reads that policy's key with `listKeys()`, builds the Event Hubs-compatible
  connection string
  (`Endpoint=<endpoint>;SharedAccessKeyName=function-telemetry-reader;SharedAccessKey=<key>;EntityPath=<name>`)
  and writes it straight into the Key Vault secret `iothub-telemetry-connection`. The key is
  never a parameter or an output, so it appears in no parameter file, deployment history or
  log.
- The app setting `IoTHubConnection` is a Key Vault reference to the secret's versionless URI,
  resolved with the managed identity. `EntityPath` in the connection string overrides the
  trigger attribute's event hub name, so `TelemetryFunction` is unchanged.
- Hub-level SAS stays enabled, because the connection depends on it, and so does device SAS,
  because the MQTT test client authenticates with device keys.

### Key Vault

Azure RBAC instead of access policies. Soft delete is at its default: enabled, 90 days.
Purge protection is **off** on purpose. Staging is torn down and recreated, and with purge
protection on, a deleted vault could not be purged: its deterministic name would stay blocked
for the whole retention period. A production environment would turn it on.

### IoT Hub

- **F1** by default: free, but only one per subscription, and it cannot be upgraded to a paid
  tier in place. Moving off F1 means creating a new hub.
- TLS 1.2 minimum. Two partitions on F1, fixed at creation, and one day of message retention.
- **No message routing.** Routes and endpoints belong to
  [#9](https://github.com/asnielsen789/IIoT-platform/issues/9).

### Monitoring

A Log Analytics workspace (30 days' retention) and a workspace-based Application Insights
resource. `APPLICATIONINSIGHTS_CONNECTION_STRING` is a plain app setting: Microsoft does not
treat it as a secret, and the portal's monitoring views need it. Ingestion by instrumentation
key stays enabled, since the worker does not authenticate to Application Insights with Entra
ID; Entra-only ingestion is an option for the security review.

## Redeploying after a teardown: Key Vault soft delete

Deleting the resource group soft-deletes the vault, and its name stays reserved for the
retention period. A redeployment then fails because a vault with the same name exists in a
deleted state. Purge it, or recover it, first:

```sh
az keyvault list-deleted --query "[].name"
az keyvault purge --name <vault-name>     # permanent
az keyvault recover --name <vault-name>   # or bring it back
```

## Open points for the first deployment

To check in [#9](https://github.com/asnielsen789/IIoT-platform/issues/9), where they can be
observed for the first time:

1. **Key Vault reference as a trigger connection.** Microsoft's documentation neither confirms
   nor rules out that Flex Consumption's scaler resolves a Key Vault reference used as a
   trigger's connection setting. After deploying, check that the reference shows as resolved,
   that the host indexes the trigger without errors, and that the app scales out from zero
   when a message arrives after it has been idle.
2. **Policy keys on redeployment.** The template restates the hub's policies without keys. The
   IoT Hub API documents that properties missing from an update can fall back to defaults, so
   a redeployment may generate new keys for all six policies. The deployment then writes the
   new connection string to Key Vault, but the Function App re-reads a Key Vault reference only
   when its configuration changes, when its references are refreshed explicitly, or after up to
   24 hours. Check by deploying twice and comparing a hash of the policy key, never the key
   itself. If the keys do change, refresh the app's Key Vault references as a post-deployment
   step.
3. **Routing.** According to the IoT Hub API reference, a template without a fallback route
   leaves it disabled, and the routing documentation says the built-in endpoint receives all
   data only while the fallback route is enabled. Until routing is configured, telemetry may
   not reach the endpoint the Function reads. The routing work should enable the fallback route
   or add an explicit route to the built-in endpoint, and confirm that messages arrive.
