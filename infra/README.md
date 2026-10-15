# Infrastructure

Bicep templates for the platform's Azure resources: IoT Hub with its message routing, Key Vault,
the Function App on a Flex Consumption plan with its storage account, and monitoring.

> [!WARNING]
> **Nothing here has been deployed yet.** The first deployment belongs to
> [#9](https://github.com/asnielsen789/IIoT-platform/issues/9) and follows [Deploy](#deploy)
> below, step by step. A pay-as-you-go subscription has no spending limit, so the budget alert
> that step 1 checks for is the only early warning of an unexpected bill. Do not run
> `az deployment sub create` without it.

## Layout

| File | Contents |
|---|---|
| `main.bicep` | Entry point, subscription scope. Creates the resource group, fixes every resource name, deploys the modules |
| `environments/staging.bicepparam` | The `staging` environment, the only one in Azure. `dev` is local development |
| `modules/identity.bicep` | User-assigned managed identity the Function App runs as |
| `modules/monitoring.bicep` | Log Analytics workspace and workspace-based Application Insights |
| `modules/storage.bicep` | Host storage and deployment package container, with the identity's storage roles |
| `modules/key-vault.bicep` | Key Vault (Azure RBAC), with the identity's read access to secrets |
| `modules/iot-hub.bicep` | IoT Hub: shared access policies, message routing, the Function's consumer group, resource logs |
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
  `Microsoft.ManagedIdentity` registered on the subscription. Step 1 of [Deploy](#deploy)
  checks them.
- To publish the Function's code: [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
  4.x, and the .NET SDK that `global.json` pins.
- To check routing and to register devices: the `azure-iot` extension for the Azure CLI.

## Build and preview

From the repository root. These commands change nothing in Azure.

```sh
# Compile and lint, offline
az bicep build --file infra/main.bicep
az bicep lint --file infra/main.bicep
az bicep build-params --file infra/environments/staging.bicepparam

# Preview the whole plan against the subscription
az deployment sub what-if \
  --location northeurope \
  --template-file infra/main.bicep \
  --parameters infra/environments/staging.bicepparam
```

`--location` is where Azure stores the deployment record; the resources take their region from
the parameter file. Treat what-if output as internal: do not paste it into issues, pull
requests or commits. On a subscription where nothing is deployed yet, the plan is 17 resources
to create.

## Deploy

The procedure for the first deployment to `staging`, and for every one after it. It is manual
until the CD pipeline ([#5](https://github.com/asnielsen789/IIoT-platform/issues/5)) automates
the same steps. Each step says what it is for and how to tell it worked; stop at the first one
that does not.

Run everything from the repository root in Git Bash. Two shell details matter on Windows:

- Azure CLI ends `-o tsv` output with a carriage return, and Git Bash keeps it in `$(...)`.
  Every captured value below is piped through `tr -d '\r'`; without that, names carry an
  invisible character and later commands fail in confusing ways.
- `$Default` is a consumer group name, not a shell variable. Keep it in single quotes.

Output stays local. The commands below ask for names and states only, but error messages and
unfiltered output contain resource IDs, and with them the subscription ID. None of it goes into
issues, pull requests or commits.

### 1. Check the prerequisites

```sh
# The right subscription, by name
az account show --query name -o tsv

# The budget alert: expect MonthlyBudget, 100, Monthly. Without it, stop here.
az consumption budget list --query "[].{name:name, amount:amount, timeGrain:timeGrain}" -o table

# Core Tools 4.x, for step 4
func --version

# The azure-iot extension, for the routing checks and device identities
az extension add --name azure-iot --upgrade

# No free hub elsewhere in the subscription: before the first deployment this prints nothing
az iot hub list --query "[?sku.name=='F1'].name" -o tsv
```

Register the resource providers. `Microsoft.ManagedIdentity` was `NotRegistered` on
2026-10-15, and the template creates a user-assigned identity. Registering is free and done
once per subscription:

```sh
az provider register --namespace Microsoft.ManagedIdentity

for ns in Microsoft.Devices Microsoft.Web Microsoft.KeyVault Microsoft.Storage \
          Microsoft.Insights Microsoft.OperationalInsights Microsoft.ManagedIdentity; do
  printf '%-30s %s\n' "$ns" "$(az provider show --namespace "$ns" --query registrationState -o tsv | tr -d '\r')"
done
```

Every line must read `Registered`. Registration takes a few minutes; repeat the loop until it
does.

### 2. Preview the plan

```sh
az deployment sub what-if \
  --location northeurope \
  --template-file infra/main.bicep \
  --parameters infra/environments/staging.bicepparam
```

The first time, expect **17 resources to create**: the resource group; the IoT Hub, its consumer
group `telemetry-function` and its diagnostic setting `routes-and-connections`; the Log
Analytics workspace and Application Insights; the Key Vault with its role assignment and the
secret `iothub-telemetry-connection`; the managed identity; the storage account with its blob
service, container `app-package` and two role assignments; the Flex Consumption plan and the
Function App. In the IoT Hub's properties, check `routing`: the route `telemetry-v1` with the
condition `$body.schemaVersion = 1` to `events`, and `fallbackRoute` enabled to `events`.
What-if masks the Function App's `siteConfig`, so its app settings are not visible here; step 6
checks them.

On a later deployment, nothing should be created. Read anything that is not `NoChange` or an
expected `Modify` before going on.

### 3. Deploy the infrastructure

```sh
DEPLOYMENT="iiot-staging-$(date -u +%Y%m%d-%H%M)"

az deployment sub create \
  --name "$DEPLOYMENT" \
  --location northeurope \
  --template-file infra/main.bicep \
  --parameters infra/environments/staging.bicepparam \
  --query "properties.{state:provisioningState, outputs:outputs}" -o json
```

This takes several minutes; the IoT Hub takes longest. It worked when `state` is `Succeeded`.
The outputs are names and the hub's host name, no IDs. A unique deployment name per run keeps
one record per deployment in the subscription's history. If the deployment fails part-way,
fix the cause and run the same command again: every resource is declared idempotently. A
failure on the managed identity means the provider registration in step 1 had not finished.

Read the names the later steps need:

```sh
output() {
  az deployment sub show --name "$DEPLOYMENT" --query "properties.outputs.$1.value" -o tsv | tr -d '\r'
}
RG=$(output resourceGroupName)
FUNC_APP=$(output functionAppName)
IOT_HUB=$(output iotHubName)
KEY_VAULT=$(output keyVaultName)
echo "$RG $FUNC_APP $IOT_HUB $KEY_VAULT"
```

In a new shell, find the last successful deployment first:

```sh
DEPLOYMENT=$(az deployment sub list \
  --query "sort_by([?starts_with(name, 'iiot-staging-') && properties.provisioningState=='Succeeded'], &properties.timestamp)[-1].name" \
  -o tsv | tr -d '\r')
```

### 4. Publish the Function's code

```sh
(cd src/IIoT.Functions && FUNCTIONS_WORKER_RUNTIME=dotnet-isolated func azure functionapp publish "$FUNC_APP")
```

Core Tools builds the project locally (`dotnet build --configuration release` into
`bin/publish`, which picks up the SDK pins in `global.json`), packages the output and deploys
it to the app's deployment container. It authenticates with the Azure CLI sign-in.

- `FUNCTIONS_WORKER_RUNTIME` tells Core Tools the project is .NET isolated. It reads that from
  the environment or from `local.settings.json`, and a fresh clone has no
  `local.settings.json`: without the variable, publishing stops with "Worker runtime is not
  set". On Flex Consumption it is not written to the app; the runtime is set in Bicep.
- If Core Tools reports that the project targets a different runtime than Azure and offers to
  update Azure, answer no: the runtime belongs to the template (`dotnet-isolated` 10.0), and a
  mismatch means the project and the template disagree.
- Never add `--publish-local-settings`. It copies `local.settings.json` into the app's
  settings, over the Key Vault reference.

It worked when Core Tools finishes without an error and the function is listed:

```sh
func azure functionapp list-functions "$FUNC_APP"
```

Expect `ProcessTelemetry`, an Event Hubs trigger. `Azure.Functions.Sdk` always uses worker
indexing, so the build output has no `functions.metadata` file, unlike the deployment payload
Microsoft describes for the older SDK; the worker reports its functions to the host at startup.
A listed function shows that this works for the package Core Tools built.

### 5. Refresh the Key Vault references (after every deployment)

Every deployment regenerates all six IoT Hub policy keys and writes a new connection string to
Key Vault ([ADR-007](../docs/adr/adr-007-telemetry-stream-access.md), update of 2026-10-12).
The Function App caches the values of its Key Vault references and fetches them again only
every 24 hours or on a configuration change, so without a refresh the trigger keeps a key that
no longer exists for up to a day. On the very first deployment the app is created after the
secret and has nothing stale; refresh anyway, so the procedure is the same every time.

```sh
KV_REFS="https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/$RG/providers/Microsoft.Web/sites/$FUNC_APP/config/configreferences/appsettings"

# Force the app to resolve every Key Vault reference again
az rest --method post --url "$KV_REFS/refresh?api-version=2022-03-01"

# The trigger's connection: expect status Resolved
az rest --method get --url "$KV_REFS/IoTHubConnection?api-version=2022-03-01" \
  --query "properties.{status:status, details:details}" -o table
```

`{subscriptionId}` stays exactly as written: `az rest` substitutes the signed-in subscription,
so the ID is never typed or stored. The full `https://` URL also keeps Git Bash from rewriting
a path that starts with `/` into a Windows path.

The refresh is the documented `POST .../config/configreferences/appsettings/refresh` operation
([Use Key Vault references as app settings](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references#understand-rotation)),
and the status comes from
[Get App Setting Key Vault Reference](https://learn.microsoft.com/rest/api/appservice/web-apps/get-app-setting-key-vault-reference).
Neither page mentions Flex Consumption specifically. Status values:

| Status | Meaning |
|---|---|
| `Resolved` | The app holds the secret's current value |
| `Initialized` | Not resolved yet; check again in a minute |
| `AccessToKeyVaultDenied` | Usually a role assignment that has not propagated yet; wait a few minutes and refresh again |
| `SecretNotFound`, `VaultNotFound` | The deployment did not complete; go back to step 3 |

Microsoft does not document whether a refresh makes running instances reconnect with the new
value. Step 7 settles it. If the Function stops processing after a redeployment although the
status is `Resolved`, restart the app and record what happened in
[#9](https://github.com/asnielsen789/IIoT-platform/issues/9):

```sh
az functionapp restart --name "$FUNC_APP" --resource-group "$RG"
```

### 6. Check the deployment

```sh
# The route and the enabled fallback route, both to events
az iot hub show --name "$IOT_HUB" --query "properties.routing.{routes:routes, fallback:fallbackRoute}" -o json

# The Function's consumer group next to $Default: expect $Default and telemetry-function
az iot hub consumer-group list --hub-name "$IOT_HUB" --query "[].name" -o tsv

# The app reads that consumer group: expect telemetry-function
az functionapp config appsettings list --name "$FUNC_APP" --resource-group "$RG" \
  --query "[?name=='IoTHubConsumerGroup'].value" -o tsv

# The hub's resource logs: expect routes-and-connections with Routes and Connections
az monitor diagnostic-settings list --resource "$IOT_HUB" --resource-group "$RG" \
  --resource-type Microsoft.Devices/IotHubs --query "[].{name:name, logs:logs[].category}" -o json
```

### 7. Confirm routing live

Routing is proven only by a message that takes it. You need a registered device: creating one
belongs to the end-to-end test ([#10](https://github.com/asnielsen789/IIoT-platform/issues/10));
follow "What you need from Azure first" in the [test client's README](../tools/IIoT.TestClient/README.md),
with `$IOT_HUB` as the hub.

Optionally, in a second terminal, watch the built-in endpoint through `$Default`, which the
Function does not use. The message should show `application/json` and `utf-8` among its system
properties:

```sh
az iot hub monitor-events --hub-name "$IOT_HUB" --consumer-group '$Default' --properties sys
```

Send one valid message with the test client. The key is read without echo, so it stays out of
the terminal and the shell history:

```sh
export IOT_HUB DEVICE_ID=tank-000001
read -rs -p "Device key: " DEVICE_KEY && export DEVICE_KEY
dotnet run --project tools/IIoT.TestClient -- --once
```

Then check three places. Metrics arrive within a few minutes; resource logs and Application
Insights can take a few minutes longer.

**The hub's routing metrics.** They tell which route the message took:

```sh
az monitor metrics list --resource "$IOT_HUB" --resource-group "$RG" \
  --resource-type Microsoft.Devices/IotHubs \
  --metrics d2c.telemetry.ingress.success d2c.endpoints.egress.builtIn.events d2c.telemetry.egress.fallback \
  --aggregation Total --interval 1h --offset 2h \
  --query "value[].{metric:name.value, total:sum(timeseries[].data[].total)}" -o table
```

| Metric | Expect | Meaning |
|---|---|---|
| `d2c.telemetry.ingress.success` | 1 per message sent | The hub accepted the messages |
| `d2c.endpoints.egress.builtIn.events` | the same | They reached the built-in endpoint, by a route or the fallback |
| `d2c.telemetry.egress.fallback` | 0 | None of them needed the fallback route |

Fallback 0 means the `telemetry-v1` route matched, so IoT Hub could query the body, so the
device's content type and encoding are right: routing and the property bag are confirmed
together. A fallback count equal to the ingress count means the route matched nothing; check
the device's property bag against section 1 of the
[payload contract](../docs/mqtt-payload.md) and read the Routes log below.

**The hub's resource logs.** In the portal, open the IoT hub, then **Monitoring > Logs**:

```kusto
AzureDiagnostics
| where ResourceProvider == "MICROSOFT.DEVICES" and Category in ("Routes", "Connections")
| where TimeGenerated > ago(2h)
| project TimeGenerated, Category, OperationName, Level, ResultDescription, properties_s
| order by TimeGenerated desc
```

Expect `Connections` rows for the test device connecting and disconnecting, and **no `Routes`
rows**: that category records problems only. `RouteEvaluationError` means a message's content
properties were missing or invalid, `UndefinedRouteEvaluation` that the body had no
`schemaVersion`.

**The Function's log.** In the portal, open Application Insights `appi-iiot-staging-neu`, then
**Logs**:

```kusto
union traces, exceptions
| where timestamp > ago(2h)
| extend text = coalesce(tostring(customDimensions.FormattedMessage), message)
| where text startswith "Device " or text startswith "Rejected message"
    or text startswith "Security event" or text startswith "Forwarding failed"
| project timestamp, itemType, severityLevel, text
| order by timestamp desc
```

Expect `Device tank-000001 measured level=...`, the reading as the Function validated it. Until a
downstream gateway is configured, it is followed by `Forwarding failed for message ...`, which
is expected: see "Configuration" in section 9 of the
[gateway contract](../docs/gateway-contract.md). A forwarding failure is logged together with its
exception, so Application Insights records it in `exceptions` rather than `traces`, with the
log text in `customDimensions.FormattedMessage`; that is why the query reads both. If nothing
appears, look for errors from the host, such as a trigger listener that could not start:

```kusto
union traces, exceptions
| where timestamp > ago(2h) and severityLevel >= 3
| project timestamp, itemType, message = coalesce(message, outerMessage)
| order by timestamp desc
```

`func azure functionapp logstream` does not support Flex Consumption; add `--browser` to open
the app's Live Metrics instead.

To check that the app scales out from zero (open point 1 below), send another message after
the app has been idle for an hour and confirm that it is processed.

### Re-deploying

Run steps 2, 3, 5 and 6 again with a new deployment name, then step 7 to confirm. Step 4 is
needed only when the code has changed: the published package stays in the deployment
container, which a deployment leaves alone.

**Step 5 is never optional.** Every deployment rotates every policy key, and the Function cannot
read the hub until its Key Vault reference is refreshed. Messages sent in between wait in the
built-in endpoint, which keeps them for one day, and the Function continues from its checkpoint
in `telemetry-function` once it reconnects.

To see the rotation for yourself (open point 2 below), compare a short hash of the Function
policy's key before and after the deployment. Never print the key itself:

```sh
az iot hub policy show --hub-name "$IOT_HUB" --name function-telemetry-reader \
  --query primaryKey -o tsv | tr -d '\r' | sha256sum | cut -c1-12
```

### Tearing down

```sh
az group delete --name "$RG" --yes
```

This deletes every resource, and frees the subscription's F1 slot with the hub. Two resources
leave something behind:

- **Key Vault** goes into soft delete for 90 days, and its name stays reserved: the next
  deployment fails because a vault with that name exists in a deleted state. Purge protection
  is off ([Key Vault](#key-vault)), so purge it, which is permanent and also removes the stale
  connection string, or recover it:

  ```sh
  az keyvault list-deleted --query "[].name" -o tsv
  az keyvault purge --name "$KEY_VAULT" --location northeurope   # permanent
  az keyvault recover --name "$KEY_VAULT"                         # or bring it back
  ```

- **The Log Analytics workspace** goes into soft delete for 14 days. The next deployment
  re-creates the resource group and then the workspace under the same name, which recovers it
  together with its old data. To start the next deployment with an empty workspace, delete the
  workspace permanently *before* deleting the resource group:

  ```sh
  az monitor log-analytics workspace delete --resource-group "$RG" \
    --workspace-name log-iiot-staging-neu --force --yes
  ```

The deployment records at subscription scope remain; they are harmless.

## Naming

Names follow the Cloud Adoption Framework abbreviations:
`<abbreviation>-<workload>-<environment>-<region>`, with workload `iiot` and region code `neu`
for North Europe. The four resources whose names are global DNS names also get a four-character
suffix, written `xxxx` below. It comes from `uniqueString()` over the subscription ID and the
environment name, so it is stable across redeployments and differs between subscriptions.

| Resource | Name in `staging` |
|---|---|
| Resource group | `rg-iiot-staging-neu` |
| Managed identity | `id-iiot-staging-neu` |
| Log Analytics workspace | `log-iiot-staging-neu` |
| Application Insights | `appi-iiot-staging-neu` |
| Storage account | `stiiotstagingneuxxxx` (storage names allow no hyphens) |
| Key Vault | `kv-iiot-staging-neu-xxxx` |
| IoT Hub | `iot-iiot-staging-neu-xxxx` |
| Flex Consumption plan | `asp-iiot-staging-neu` |
| Function App | `func-iiot-staging-neu-xxxx` |

Key Vault names are limited to 24 characters, which is why the environment name is limited to
seven. Every resource carries the tags `workload`, `environment` and `managed-by: bicep`.

## Design notes

### Region: North Europe

West Europe offers everything this platform needs, but Azure currently closes it to tenants that
have no resources there yet ([region access policy](https://learn.microsoft.com/azure/azure-resource-manager/troubleshooting/error-region-access-policy)):
a what-if there fails with `RequestDisallowedByAzure`. Sweden Central does not offer IoT Hub.
North Europe offers IoT Hub and Flex Consumption with .NET 10, and keeps the platform in the EU.

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
  trigger attribute's event hub name, so the attribute keeps `messages/events`.
- The trigger reads through its own consumer group, set by the app setting
  `IoTHubConsumerGroup`; see [Message routing](#message-routing).
- Hub-level SAS stays enabled, because the connection depends on it, and so does device SAS,
  because the MQTT test client authenticates with device keys.

### Message routing

Declared in `modules/iot-hub.bicep` rather than configured by hand
([#9](https://github.com/asnielsen789/IIoT-platform/issues/9)). Both routes lead to the built-in
endpoint that the Function reads (ADR-007); there are no custom endpoints.

| Route | Source | Condition | Endpoint |
|---|---|---|---|
| `telemetry-v1` | Device messages | `$body.schemaVersion = 1` | `events` (built-in) |
| `$fallback` (fallback route) | Device messages | Anything no route matched | `events` (built-in) |

- **Why route on the body.** IoT Hub evaluates a query on the message body only when the device
  sets `contentType` to `application/json` and `contentEncoding` to a UTF encoding, which
  section 1 of the [payload contract](../docs/mqtt-payload.md) requires. A message delivered
  through `telemetry-v1` is therefore live proof that the device's MQTT property bag is right.
- **Why the fallback route, explicitly.** Once any route exists, the built-in endpoint receives
  only what a route sends it, IoT Hub drops a message that matches no route unless the fallback
  route is enabled, and a template that leaves it out leaves it disabled. With it, a message of
  another schema version, or from a device that omitted the content properties, still reaches
  the Function, which rejects it with a logged reason instead of IoT Hub discarding it
  silently. Microsoft's documentation is ambiguous about what a hub without any routing
  configuration delivers, so the template does not rely on defaults.
- **Telling the two apart.** Both lead to the same endpoint, so the hub records which one a
  message took: the metric *Routing: messages delivered to fallback*
  (`d2c.telemetry.egress.fallback`) counts the messages no route matched, and the Routes
  resource log says why a route could not be evaluated. Step 7 of [Deploy](#deploy) uses both.
- **F1 limits.** A free hub allows five routing queries and one extra endpoint. This uses one
  route; routes to the built-in endpoint need no extra endpoint.
- **A consumer group of the Function's own.** `telemetry-function`, on the built-in endpoint.
  Readers in one consumer group compete for its partitions, and `az iot hub monitor-events`
  reads `$Default`, so the Function stays out of it. A hub allows 20 consumer groups on the
  built-in endpoint, F1 included. The trigger takes the name from the app setting
  `IoTHubConsumerGroup` (`ConsumerGroup = "%IoTHubConsumerGroup%"` in `TelemetryFunction`).

**Local development.** The setting has no default: without it the trigger cannot start, and
the host reports `'%IoTHubConsumerGroup%' does not resolve to a value`. Add it to `Values` in
`src/IIoT.Functions/local.settings.json`, next to `IoTHubConnection`. That file is gitignored
and must stay that way. Use `$Default`, or a consumer group of your own, and never
`telemetry-function` while the deployed app is running: the two would compete for its
partitions. `$Default` is shared with `az iot hub monitor-events`, so do not run both at once.

### Key Vault

Azure RBAC instead of access policies. Soft delete is at its default: enabled, 90 days.
Purge protection is **off** on purpose. Staging is torn down and recreated, and with purge
protection on, a deleted vault could not be purged: its deterministic name would stay blocked
for the whole retention period. A production environment would turn it on.

### IoT Hub

- **F1** by default: free, but only one per subscription, and it cannot be upgraded to a paid
  tier in place. Moving off F1 means creating a new hub.
- TLS 1.2 minimum. Two partitions on F1, fixed at creation, and one day of message retention.
- Message routing to the built-in endpoint and a consumer group for the Function: see
  [Message routing](#message-routing).

### Monitoring

A Log Analytics workspace (30 days' retention) and a workspace-based Application Insights
resource. `APPLICATIONINSIGHTS_CONNECTION_STRING` is a plain app setting: Microsoft does not
treat it as a secret, and the portal's monitoring views need it. Ingestion by instrumentation
key stays enabled, since the worker does not authenticate to Application Insights with Entra
ID; Entra-only ingestion is an option for the security review.

The IoT Hub sends two resource log categories to the same workspace, where they land in the
`AzureDiagnostics` table:

- **Routes**: route evaluation problems and endpoint health. It is the only record of a
  routing failure, which is what makes one visible. Successful deliveries are not logged, so a
  healthy hub writes nothing here.
- **Connections**: device connects and disconnects, including failed attempts.

The other categories are left off. Platform metrics, the routing metrics included, need no
diagnostic setting.

## Open points for the first deployment

To check in [#9](https://github.com/asnielsen789/IIoT-platform/issues/9), where they can be
observed for the first time:

1. **Key Vault reference as a trigger connection.** Microsoft's documentation neither confirms
   nor rules out that Flex Consumption's scaler resolves a Key Vault reference used as a
   trigger's connection setting. Step 5 shows the reference resolved; step 7 shows that the
   trigger connects, and that the app scales out from zero when a message arrives after it has
   been idle.
2. **Policy keys on redeployment.** ADR-007's update concludes from the IoT Hub API reference
   that every deployment generates new keys for all six policies. Confirm it with the key hash
   under [Re-deploying](#re-deploying), and confirm with step 7 that the Function keeps
   processing after the step 5 refresh.
3. **Routing on the message body.** The first message from the test client either takes the
   `telemetry-v1` route, which confirms routing and the documented MQTT property bag together,
   or lands in the fallback count, which points at the property bag. Step 7 tells which.
