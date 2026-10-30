# Infrastructure

Bicep templates for the platform's Azure resources: IoT Hub with its message routing, Key Vault,
the Function App on a Flex Consumption plan with its storage account and dead-letter queues, and
monitoring.

> [!WARNING]
> **Infrastructure is deployed by hand,** following [Deploy](#deploy) below, step by step; the
> pipeline deploys only the Function's code ([Continuous deployment](#continuous-deployment)).
> Staging was first deployed on 2026-10-15
> ([#9](https://github.com/asnielsen789/IIoT-platform/issues/9)). A pay-as-you-go subscription
> has no spending limit, so the budget alert that step 1 checks for is the only early warning of
> an unexpected bill. Do not run `az deployment sub create` without it.

## Layout

| File | Contents |
|---|---|
| `main.bicep` | Entry point, subscription scope. Creates the resource group, fixes every resource name, deploys the modules |
| `environments/staging.bicepparam` | The `staging` environment, the only one in Azure. `dev` is local development |
| `modules/identity.bicep` | User-assigned managed identity the Function App runs as |
| `modules/monitoring.bicep` | Log Analytics workspace and workspace-based Application Insights |
| `modules/storage.bicep` | Host storage, deployment package container and the dead-letter queues `forwarding-failed` and `forwarding-failed-poison`, with the identity's storage roles ([Dead-letter queues](#dead-letter-queues)) |
| `modules/key-vault.bicep` | Key Vault (Azure RBAC), with the identity's read access to secrets |
| `modules/device-ca-keys.bicep` | The device certificate authority's root and issuing CA keys, in that vault ([ADR-009](../docs/adr/adr-009-device-pki.md)) |
| `modules/iot-hub.bicep` | IoT Hub: shared access policies, message routing, the Function's consumer group, resource logs |
| `modules/iot-hub-connection-secret.bicep` | Builds the Function's IoT Hub connection string and writes it to Key Vault |
| `modules/function-app.bicep` | Flex Consumption plan and Function App |
| `modules/gateway-mock.bicep` | The gateway mock on App Service (free tier), the Function's downstream gateway in staging ([Gateway mock](#gateway-mock)) |
| `modules/provisioning-service.bicep` | The Device Provisioning Service, linked to the IoT Hub through the hub's `dps-link` policy, for zero-touch provisioning ([Device Provisioning Service](#device-provisioning-service)) |
| `modules/github-deployment.bicep` | Managed identity the deploy workflow logs in with: its GitHub federated credential and its role on the Function App |

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
- To publish the Function's code by hand, the fallback to [continuous deployment](#continuous-deployment):
  [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
  4.x, and the .NET SDK that `global.json` pins.
- To set up continuous deployment once: the GitHub CLI (`gh`), signed in with admin rights on
  the repository.
- To check routing and to register devices: the `azure-iot` extension for the Azure CLI.
- To run the device certificate authority: the .NET SDK that `global.json` pins, and the roles
  in [Device certificate authority (one-time)](#device-certificate-authority-one-time).

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
requests or commits. On a subscription where nothing is deployed yet, the plan is 33 resources
to create.

## Deploy

The procedure for the first deployment to `staging`, and for every infrastructure deployment
after it. The infrastructure is always deployed by hand, with these steps. The Function's code
is normally deployed by the pipeline ([Continuous deployment](#continuous-deployment)), which
also refreshes the Key Vault references after each code deployment; step 4 is the manual
fallback. Each step says what it is for and how to tell it worked; stop at the first one that
does not.

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

The first time, expect **33 resources to create**: the resource group; the IoT Hub, its consumer
group `telemetry-function` and its diagnostic setting `routes-and-connections`; the Log
Analytics workspace and Application Insights; the Key Vault with its role assignment, the
secret `iothub-telemetry-connection` and the device CA keys `device-ca-root` and
`device-ca-issuing`; the managed identity; the storage account with its blob
service, container `app-package`, queue service, queues `forwarding-failed` and
`forwarding-failed-poison` and four role assignments, two of them on the queues; the Flex
Consumption plan and the Function App; the deployment identity `id-iiot-staging-neu-github`, its federated credential
`github-staging` and its role assignment on the Function App; the gateway mock's App Service
plan and web app, its two basic-auth publishing policies (`scm`, `ftp`) and its log settings;
the Device Provisioning Service. In the IoT Hub's properties, check
`routing`: the route `telemetry-v1` with the condition `$body.schemaVersion = 1` to `events`,
and `fallbackRoute` enabled to `events`. In the federated credential, check `subject`: it must
end in `:environment:staging`. What-if masks the Function App's `siteConfig`, so its app
settings are not visible here; step 6 checks them.

On a later deployment, nothing should be created. Read anything that is not `NoChange` or an
expected `Modify` before going on. The first deployment after continuous deployment was added
is the exception: it creates the deployment identity, its federated credential and its role
assignment, three resources, and changes nothing else. So is the first deployment after the
device certificate authority was added: it creates the two CA keys and changes nothing else. The
first deployment after the workspace's daily cap was added modifies the Log Analytics workspace
(`workspaceCapping`) and nothing else ([Monitoring](#monitoring)). The first deployment after
the gateway credentials were added modifies the Function App's app settings, four
`GatewayAuth__*` settings, and nothing else; write the two secrets first
([Service-to-service authentication (one-time)](#service-to-service-authentication-one-time)).
The first deployment after the gateway mock was added creates its five resources and adds
`GatewayV1__BaseAddress` to the Function App's settings; then publish the mock
([Gateway mock](#gateway-mock)), since until then the Function forwards to an empty app. The
first deployment after dead-lettering was added creates five resources, the storage account's
queue service, the queues `forwarding-failed` and `forwarding-failed-poison` and the identity's
role assignment on each, adds three `DeadLetterStorage__*` settings to the Function App, and
changes nothing else ([Dead-letter queues](#dead-letter-queues)). Deploy it before the code that
uses it reaches the app; the other way round, `RetryForwarding` stays disabled until the settings
exist. The first deployment after the Device Provisioning Service was added creates it, adds the
policy `dps-link` to the IoT Hub, and changes nothing else
([Device Provisioning Service](#device-provisioning-service)).
`what-if` does not show app settings at all, since Azure does not return them with the site, so
that change is not in the plan; step 5 checks it afterwards. Once the CA keys exist, `what-if`
also lists them as `Modify` with `rotationPolicy` to be deleted: noise, like the role
assignments' `principalId`. On later deployments the keys stay as they are: Azure creates a key only if none exists by that
name, and never updates an existing one or adds a version to it
([Device certificate authority](#device-certificate-authority)).

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
The outputs are names, the hub's host name and, in `githubDeploymentClientId`, the client ID of
the deployment identity: an identifier, not a credential, but it stays out of the repository
like every other ID. No resource ID is among them. A unique deployment name per run keeps
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

### 4. Publish the Function's code (fallback)

Normally the pipeline does this: every change to the Function that reaches `main` is deployed
by `.github/workflows/deploy.yml`, and a manual run of that workflow redeploys `main` at any
time ([Continuous deployment](#continuous-deployment)). An infrastructure deployment leaves the
published package in place, so after one there is nothing to republish. Publish by hand only
when the pipeline cannot run, such as before its one-time setup is done, or to try a build that
is not on `main`. The package the pipeline builds has the same files as the one Core Tools
builds.

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

### 5. Refresh the Key Vault references (after every infrastructure deployment)

Every infrastructure deployment regenerates all six IoT Hub policy keys and writes a new
connection string to Key Vault
([ADR-007](../docs/adr/adr-007-telemetry-stream-access.md), update of 2026-10-12).
The Function App caches the values of its Key Vault references and fetches them again only
every 24 hours or on a configuration change, so without a refresh the trigger keeps a key that
no longer exists for up to a day. On the very first deployment the app is created after the
secret and has nothing stale; refresh anyway, so the procedure is the same every time.

The pipeline runs the same two calls after every code deployment, but it runs only when the
code changes, so it does not cover this step. A code deployment rotates no key; the pipeline
refreshes to keep the procedure identical and to check that the reference still resolves.

```sh
KV_REFS="https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/$RG/providers/Microsoft.Web/sites/$FUNC_APP/config/configreferences/appsettings"

# Force the app to resolve every Key Vault reference again
az rest --method post --url "$KV_REFS/refresh?api-version=2022-03-01"

# The trigger's connection and the gateway credentials: expect Resolved for each
for setting in IoTHubConnection GatewayAuth__ClientId GatewayAuth__ClientSecret; do
  printf '%s: ' "$setting"
  az rest --method get --url "$KV_REFS/$setting?api-version=2022-03-01" --query "properties.status" -o tsv
done
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
value. Step 7 settles it. On 2026-10-25, after a redeployment while an instance was running, the
next message was processed, yet nothing for that execution reached Application Insights; the next
instance to start logged normally. So before concluding that the Function has stopped, check the
app's own execution count, which does not depend on Application Insights:

```sh
az monitor metrics list --resource "$FUNC_APP" --resource-group "$RG" \
  --resource-type Microsoft.Web/sites --metrics OnDemandFunctionExecutionCount \
  --aggregation Total --interval 1m --offset 30m \
  --query "value[0].timeseries[0].data[?total>\`0\`].{time:timeStamp, executions:total}" -o table
```

If the Function really has stopped processing although the status is `Resolved`, restart the
app and record what happened in [#9](https://github.com/asnielsen789/IIoT-platform/issues/9):

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

# The dead-letter queues: expect forwarding-failed and forwarding-failed-poison
STORAGE=$(output storageAccountName)
az rest --method get \
  --url "https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STORAGE/queueServices/default/queues?api-version=2023-05-01" \
  --query "value[].name" -o tsv

# Their connection settings: expect the three DeadLetterStorage__ names
az functionapp config appsettings list --name "$FUNC_APP" --resource-group "$RG" \
  --query "[?starts_with(name, 'DeadLetterStorage__')].name" -o tsv
```

The queue listing reads the storage account through Azure Resource Manager, as step 5 reads the
app, so it needs no data-plane role. `{subscriptionId}` stays as written, as in step 5.

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

Run steps 2, 3, 5 and 6 again with a new deployment name, then step 7 to confirm. Step 4 is not
needed: the published package stays in the deployment container, which an infrastructure
deployment leaves alone, and code changes reach the app through the pipeline.

**Step 5 is never optional.** Every infrastructure deployment rotates every policy key, and the
Function cannot read the hub until its Key Vault reference is refreshed. Messages sent in
between wait in the built-in endpoint, which keeps them for one day, and the Function continues
from its checkpoint in `telemetry-function` once it reconnects.

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
  connection string, or recover it. **Purging also deletes the gateway credentials**: write
  them again before the next deployment
  ([Service-to-service authentication (one-time)](#service-to-service-authentication-one-time)).
  **Purging also destroys the device CA keys.** The next
  deployment creates new ones, and every certificate the old CA issued stops working: run
  [Device certificate authority (one-time)](#device-certificate-authority-one-time) again,
  with `init --force`, and issue every device a new certificate. Recovering the vault keeps the
  CA:

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

The deployment records at subscription scope remain; they are harmless. The deployment identity
goes with the resource group, and the next deployment creates a new one with a new client ID:
see [After the infrastructure is recreated](#after-the-infrastructure-is-recreated).

## Gateway mock

Staging has no real downstream system, so the Function forwards to the gateway mock
([`tools/IIoT.GatewayMock`](../tools/IIoT.GatewayMock/README.md)) on App Service
([#15](https://github.com/asnielsen789/IIoT-platform/issues/15)). The templates create the app
and set the Function's `GatewayV1__BaseAddress` to it, and the mock's issuer and audience to the
Function's identity provider. The code is published by hand, like an infrastructure change: the
deploy workflow covers the Function only.

Publish after the first infrastructure deployment that creates the app, and after any change to
the mock. Deployment signs in with your Azure CLI session, since the app has basic-auth
publishing turned off:

```sh
MOCK_APP=$(output gatewayMockAppName)
OUT="$TEMP/gateway-mock" ZIP="$TEMP/gateway-mock.zip"
rm -rf "$OUT" "$ZIP"
dotnet publish tools/IIoT.GatewayMock -c Release -o "$OUT"
powershell.exe -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$OUT")\*' -DestinationPath '$(cygpath -w "$ZIP")' -Force"
az webapp deploy --resource-group "$RG" --name "$MOCK_APP" --src-path "$ZIP" --type zip
```

`Compress-Archive` builds the zip, because Git Bash has no `zip`. The deployment ends with "Site
started successfully". Then check that the mock refuses an unauthenticated call. `401` is right:
it means the mock is running and its JWT validation is on.

```sh
curl -s -o /dev/null -w "%{http_code}\n" "https://$MOCK_APP.azurewebsites.net/v1/readings" -X POST
```

**End to end.** Send a message as in step 7 of [Deploy](#deploy). The Function's log then shows
`Delivered message … to gateway v1 (202)`, and its dependencies show the token request to the
identity provider (`POST /oauth/token`, `200`) and the call to the mock
(`POST /v1/readings`, `202`). The mock logs `Accepted message <messageId> from <device> …,
delivered by <client-id>@clients`, which you can follow with:

```sh
az webapp log tail --resource-group "$RG" --name "$MOCK_APP"
```

The mock keeps what it accepts in memory, so a restart, or the app unloading when idle, empties
it.

## Dead-lettered messages

A message that passed validation and the identity check, but could not be forwarded even after
the forwarder's own retries, is dead-lettered
([ADR-010](../docs/adr/adr-010-dead-letter-handling.md)): `ProcessTelemetry` writes it to the queue
`forwarding-failed`, and `RetryForwarding` forwards it again, up to eight times, 30 minutes apart.
An entry the retries give up on, or one a retry refuses for good, ends in
`forwarding-failed-poison`. Nothing in the platform reads that queue. An operator decides, with
the commands below, whether to put an entry back for another round or to delete it. Entries do
not expire, so nothing is lost by waiting.

Verified on staging on 2026-10-27 and 2026-10-30
([#19](https://github.com/asnielsen789/IIoT-platform/issues/19)), with the gateway mock stopped and
a shortened schedule (1 minute, 3 attempts):
- **Dead-lettering:** a failed forward was dead-lettered, never to expire.
- **Retries:** the queue retried it on schedule, and a failed retry went back to the queue rather
  than being dead-lettered again.
- **Poison:** after the last attempt the entry was moved to `forwarding-failed-poison`, again
  never to expire.
- **Replay:** with the commands below, the replayed entry was delivered on its first attempt. The
  gateway recorded the original receipt time.

Run from the repository root in Git Bash, in the shell where `RG` and `output` are set (step 3 of
[Deploy](#deploy)).

### Following it in the logs

In Application Insights `appi-iiot-staging-neu`, **Logs**:

```kusto
union traces, exceptions
| where timestamp > ago(1d)
| extend text = coalesce(tostring(customDimensions.FormattedMessage), message)
| where text contains "dead-letter" or text contains "forwarding-failed"
| project timestamp, severityLevel, text
| order by timestamp desc
```

| Line | Meaning |
|---|---|
| `Dead-lettered message … to forwarding-failed` | The first attempt failed; the first retry follows in about 30 minutes |
| `Retrying dead-lettered message … attempt N of 8` | A retry started |
| `Attempt N of 8 for dead-lettered message … failed` | It failed; the queue retries it after the interval |
| `Delivered dead-lettered message … on attempt N` | A retry delivered it; the entry is deleted |
| `Moved dead-lettered message … to forwarding-failed-poison` | Given up, with the reason: the last attempt failed, the payload was rejected on retry, or the entry could not be read |
| `Message … is lost` (severity 4, Critical) | Forwarding failed and the entry could not be written, because it is too large for a queue message or the write failed. The log line is the only record |
| `Message has reached MaxDequeueCount of 8. Moving message to queue 'forwarding-failed-poison'` | The runtime's own move, after `RetryForwarding` could not move the entry itself. The entry expires seven days later ([step 2](#2-read-the-poison-queue)) |

### 1. Give yourself the queue roles

Two roles, each on one queue, the narrowest scope Azure RBAC offers for queue data. The actions
are those of
[Permissions for calling data operations](https://learn.microsoft.com/rest/api/storageservices/authorize-with-azure-active-directory#permissions-for-queue-service-operations):

| Role | Scope | Needed for |
|---|---|---|
| Storage Queue Data Message Processor | Queue `forwarding-failed-poison` | Reading entries without changing them (`peek`: `messages/read`), and taking one and deleting it (`get`, `delete`: `messages/process/action`) |
| Storage Queue Data Message Sender | Queue `forwarding-failed` | Putting an entry back (`put`: `messages/add/action`). Only for a replay |

**Message Sender on `forwarding-failed` amounts to the right to assert a device's identity.**
`RetryForwarding` forwards an entry under the `authenticatedDeviceId` the entry carries, so whoever
can write to that queue can file readings for any device. Assign it for a replay and remove it
straight after; reading the poison queue needs only Message Processor.

```sh
STORAGE=$(output storageAccountName)
ME=$(az ad signed-in-user show --query id -o tsv | tr -d '\r')
ST_ID=$(az storage account show --name "$STORAGE" --resource-group "$RG" --query id -o tsv | tr -d '\r')
QUEUES="$ST_ID/queueServices/default/queues"

MSYS_NO_PATHCONV=1 az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Storage Queue Data Message Processor" --scope "$QUEUES/forwarding-failed-poison" -o none
```

`MSYS_NO_PATHCONV=1` is needed on the role commands only, whose scope starts with
`/subscriptions/` ([why](#1-assign-the-operators-roles)). None of the `az storage message`
arguments below starts with `/`: an entry's content always starts with `eyJ`, the base64 of `{"`,
and a message ID is a GUID. A new role assignment can take a few minutes to apply, up to 30
according to Microsoft.

### 2. Read the poison queue

```sh
# What waits, without changing anything: up to 32 entries per call
az storage message peek --queue-name forwarding-failed-poison --account-name "$STORAGE" --auth-mode login \
  --num-messages 32 --query "[].{id:id, inserted:insertionTime, expires:expirationTime}" -o table

# The first entry's envelope
az storage message peek --queue-name forwarding-failed-poison --account-name "$STORAGE" --auth-mode login \
  --query "[0].content" -o tsv | tr -d '\r' | base64 -d; echo
```

- **The content is base64**, the encoding the Functions queue trigger expects by default. The Azure
  CLI stores and returns message text as it is, without encoding or decoding it, so `base64 -d`
  shows the entry.
- **The entry is the envelope**: `envelopeVersion`, `messageId`, `authenticatedDeviceId`,
  `enqueuedAt`, `deadLetteredAt`, `failure` with the first attempt's reason and status code, and
  the `payload` exactly as the device sent it, as an escaped JSON string. Why it ended in the poison
  queue is in the Function's log, above. The payload is the device's readings and metadata: keep
  it local, like what-if output.
- **Check `expires` against `inserted`.** The platform writes every entry without expiry. An entry
  that expires exactly seven days after it was inserted, the service's default, was moved by the
  runtime itself, which happens only when `RetryForwarding`'s own move failed. It is gone after that
  date, so handle it first.

### 3. Put an entry back, or delete it

First fix what made it fail. An entry rejected on retry, such as a payload of a schema version the
deployed build no longer accepts, is rejected again until a build that accepts it is deployed. A
replay after a gateway outage needs the gateway to be up.

```sh
# Take the first entry: invisible to everyone else for five minutes, long enough for the rest
read -r ID RECEIPT CONTENT < <(az storage message get --queue-name forwarding-failed-poison \
  --account-name "$STORAGE" --auth-mode login --visibility-timeout 300 \
  --query "[0:1].[id, popReceipt, content]" -o tsv | tr -d '\r')
printf '%s' "$CONTENT" | base64 -d | head -c 300; echo
```

The query is `[0:1].[…]`, a list holding one row, so the CLI's tab-separated output prints the three
values on one line for `read`. With `[0].[…]`, a flat list, it prints one value per line, and only
`ID` is set. The `get` still takes the entry, which then stays invisible for the five minutes. That
happened on the first run. `head -c` cuts the base64 off mid-way, so `base64` may report "invalid
input" after the decoded text; the content itself is intact.

To replay it, assign Message Sender on `forwarding-failed`, put the content back exactly as it
came, still base64, without expiry, then delete it from the poison queue and remove the role:

```sh
MSYS_NO_PATHCONV=1 az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Storage Queue Data Message Sender" --scope "$QUEUES/forwarding-failed" -o none

az storage message put --queue-name forwarding-failed --account-name "$STORAGE" --auth-mode login \
  --content "$CONTENT" --time-to-live -1 -o none
az storage message delete --queue-name forwarding-failed-poison --account-name "$STORAGE" --auth-mode login \
  --id "$ID" --pop-receipt "$RECEIPT"

MSYS_NO_PATHCONV=1 az role assignment delete --assignee "$ME" \
  --role "Storage Queue Data Message Sender" --scope "$QUEUES/forwarding-failed"
```

- **Put before delete.** If the `put` fails, the entry reappears in the poison queue after five
  minutes. If the `delete` fails after a `put`, the entry is replayed and also stays in the poison
  queue; replaying it once more later does no harm, since the gateway stores a batch once.
- **The content goes back unchanged.** Decoded or edited text would no longer be what the trigger
  expects: text that is not base64 is moved straight back to the poison queue by the runtime.
- **A replayed entry starts again at attempt 1**, so it gets eight more attempts, the first within
  about a minute while the app runs. The log shows `Retrying dead-lettered message … attempt 1`.
- **A very large entry cannot be put back from the command line.** Its base64 is passed as one
  argument, and Windows limits a command line to 32,767 characters, so an entry above about 24 KB
  needs another client, such as Azure Storage Explorer signed in with Microsoft Entra ID. A message
  of the size the contract describes is far below that.

To discard an entry instead, run only the `delete`. Remove the Message Processor role when you are
done:

```sh
MSYS_NO_PATHCONV=1 az role assignment delete --assignee "$ME" \
  --role "Storage Queue Data Message Processor" --scope "$QUEUES/forwarding-failed-poison"
```

## Continuous deployment

`.github/workflows/deploy.yml` deploys the Function's code to `staging`
([#5](https://github.com/asnielsen789/IIoT-platform/issues/5)). It runs on every push to `main`
that changes the deployed package or how it is built: `src/`, the telemetry schema that
`IIoT.Core` embeds, `global.json`, the solution, the root build files and the workflow itself.
It also runs on demand, from the Actions tab or with `gh workflow run deploy.yml --ref main`. The
infrastructure is not part of it; [Deploy](#deploy) stays manual.

The workflow has two jobs:

1. **Build, test and package.** Restore, Release build and the whole test suite, as in CI, then
   `dotnet publish --no-build` of `src/IIoT.Functions`, kept as a workflow artifact for 7 days.
   The package holds the very assemblies the tests ran against. It has the same files as the
   package Core Tools builds in step 4, less `IIoT.Core.deps.json`, which the host does not
   read.
2. **Deploy to staging.** Runs in the GitHub environment `staging`, which lists every deployment
   with its commit on the repository's Environments page. It checks the package layout, logs in
   to Azure with OIDC, deploys with `Azure/functions-action` (One Deploy, no remote build), makes
   the two calls of step 5 and waits for `IoTHubConnection` to report `Resolved`, then syncs the
   triggers until `ProcessTelemetry` is listed. Each wait gives up after about five minutes and
   fails the run with the reason.

Runs never overlap, and a deployment that has started is never cancelled. Only the deploy job
can obtain an Azure token.

### Trust model

The workflow logs in as the user-assigned managed identity `id-iiot-staging-neu-github`, which
the templates create for it alone (`modules/github-deployment.bicep`, roles under
[Identity and access](#identity-and-access)). No secret is involved: the job requests an OIDC
token from GitHub, and Microsoft Entra ID exchanges it for an access token only if the token's
issuer, audience and subject match the identity's federated credential `github-staging`. The
subject is:

```text
repo:asnielsen789@40421965/IIoT-platform@1371071633:environment:staging
```

GitHub names repositories created after 15 July 2026 by their immutable owner and repository IDs
as well as their names, so another repository that later takes over the name cannot match. The
prefix comes from `githubOidcSubjectPrefix` in `environments/staging.bicepparam`. The subject
names the GitHub environment, not a branch. The environment's deployment branch policy, which
admits only `main`, is what keeps every other branch out: it is part of the security boundary,
and the setup below creates it.

### One-time setup

Create the GitHub environment first, and its secrets and variables once an infrastructure
deployment has created the deployment identity (steps 1 to 6 of [Deploy](#deploy)). The
environment does not depend on Azure, and creating it first puts its branch policy in place
before the federated credential that relies on it exists. This needs admin rights on the repository. Run from the
repository root in Git Bash: `{owner}/{repo}` is literal, and `gh` fills in this repository. The
values go from the Azure CLI straight into GitHub and are never written to a file.

Until the setup is complete, a run of the workflow builds and tests, then fails in the deploy
job, either at the settings check or at the login. That includes the run that merging this
workflow into `main` starts.

1. Create the environment `staging` and allow deployments from `main` only:

   ```sh
   gh api -X PUT 'repos/{owner}/{repo}/environments/staging' \
     -F 'deployment_branch_policy[protected_branches]=false' \
     -F 'deployment_branch_policy[custom_branch_policies]=true' --jq .name
   gh api -X POST 'repos/{owner}/{repo}/environments/staging/deployment-branch-policies' \
     -f name=main -f type=branch --jq .name
   ```

   The commands print `staging` and `main`. The name must be exactly `staging`, in lower case:
   it becomes part of the token's subject, and Entra ID compares subjects exactly. A workflow
   run that references an environment that does not exist creates it without any branch
   policy; the `PUT` above then adds the policy, so it is safe to run either way.

2. After the infrastructure deployment, read the values, in the shell where `DEPLOYMENT` and
   `output` from step 3 are defined:

   ```sh
   CLIENT_ID=$(output githubDeploymentClientId)
   RG=$(output resourceGroupName)
   FUNC_APP=$(output functionAppName)
   TENANT_ID=$(az account show --query tenantId -o tsv | tr -d '\r')
   SUBSCRIPTION_ID=$(az account show --query id -o tsv | tr -d '\r')
   ```

3. Set the three IDs as environment secrets and the two names as environment variables:

   ```sh
   gh secret set AZURE_CLIENT_ID --env staging --body "$CLIENT_ID"
   gh secret set AZURE_TENANT_ID --env staging --body "$TENANT_ID"
   gh secret set AZURE_SUBSCRIPTION_ID --env staging --body "$SUBSCRIPTION_ID"
   gh variable set AZURE_RESOURCE_GROUP --env staging --body "$RG"
   gh variable set AZURE_FUNCTIONAPP_NAME --env staging --body "$FUNC_APP"

   # Names only: expect the three secrets and the two variables
   gh secret list --env staging --json name --jq '.[].name'
   gh variable list --env staging --json name --jq '.[].name'
   ```

4. Run the workflow on `main`, then follow it in the Actions tab or with `gh run watch`:

   ```sh
   gh workflow run deploy.yml --ref main
   ```

   It worked when both jobs pass and the environment `staging` shows the deployment with its
   commit. A smoke check is not proof of processing; confirm with step 7 of [Deploy](#deploy).

None of the three IDs is a credential: without the federated credential, a client ID grants
nothing. They are secrets only because the repository is public, and so are its workflow logs:
GitHub prints the inputs of every step and masks secrets, not variables. As secrets, the tenant
and subscription IDs stay out of the logs, as they stay out of the repository. The resource group
and app names are not sensitive and stay variables.

### After the infrastructure is recreated

Tearing down deletes the deployment identity with everything else, and the next deployment
creates a new one with a new client ID. The names of the resource group and the app are
deterministic and do not change. After steps 1 to 6 of [Deploy](#deploy), read `CLIENT_ID` as in
step 2 above, set the secret `AZURE_CLIENT_ID` again as in step 3, and run the workflow: the recreated app
has an empty deployment container until it does.

### When the login or the deployment fails

- **No matching federated identity record.** Entra ID rejected the token because its subject
  differs from the credential's. The error message quotes the subject GitHub presented; compare
  it with the credential's, and with the prefix GitHub uses for this repository:

  ```sh
  az identity federated-credential list --identity-name id-iiot-staging-neu-github \
    --resource-group "$RG" --query "[].subject" -o tsv
  gh api 'repos/{owner}/{repo}/actions/oidc/customization/sub' --jq .sub_claim_prefix
  ```

  A repository that is renamed or transferred moves to the immutable form as well. Put the new
  prefix into `environments/staging.bicepparam` and deploy the infrastructure.
- **Branch not allowed to deploy.** A job on another branch referenced the environment, and its
  branch policy refused it, as it should. (`deploy.yml` itself skips the deploy job off `main`.)
- **AuthorizationFailed** right after the identity was created: its role assignment has not
  propagated yet. Re-run the failed job a few minutes later.

## Service-to-service authentication (one-time)

The Function authenticates to the downstream gateway with an OAuth 2.0 access token, obtained
with the client-credentials grant
([`gateway-contract.md`](../docs/gateway-contract.md), section 6). Staging uses an Auth0 tenant;
the token provider is not specific to Auth0, and another provider is a matter of the same
settings. Four app settings carry the configuration:

| Setting | Value | Where it comes from |
|---|---|---|
| `GatewayAuth__TokenEndpoint` | `https://<tenant domain>/oauth/token` | `gatewayAuthTokenEndpoint` in the parameter file. Public |
| `GatewayAuth__Audience` | The gateway API's identifier | `gatewayAuthAudience` in the parameter file. Public: every token carries it as `aud` |
| `GatewayAuth__ClientId` | The application's client ID | Key Vault reference to the secret `gateway-auth-client-id` |
| `GatewayAuth__ClientSecret` | The application's client secret | Key Vault reference to the secret `gateway-auth-client-secret` |

The client ID is not a credential, but it stays out of the repository like every other ID. The
templates only reference the two secrets; the operator writes them, so neither value is in the
repository or in the deployment history.

### 1. At the identity provider

In Auth0, once per tenant:

- **An API** for the gateway. Its identifier becomes the audience, and is never called, so a
  reserved domain such as `https://gateway.iiot.example` serves. Signing algorithm **RS256**,
  which the gateway mock requires.
- **A machine-to-machine application** for the Function, authorised for that API, with no
  permissions. It is first-party: leave "owned by a third party" off. Its grant types are client
  credentials only, and its authentication method is **Client Secret (Post)**, which is the token
  provider's default (`client_secret_post`).

Set `gatewayAuthTokenEndpoint` and `gatewayAuthAudience` in the parameter file. The tenant's
discovery document, `https://<tenant domain>/.well-known/openid-configuration`, shows the issuer
(with its trailing slash) and the signing keys.

### 2. Write the credentials into Key Vault

Before the first infrastructure deployment that references them: until both secrets exist, the
references do not resolve, and the deploy workflow's check fails. Writing secrets needs Key Vault
Secrets Officer, on the vault, since the secrets do not exist yet to scope it to; it is removed
again afterwards. `MSYS_NO_PATHCONV=1` is explained in
[Device certificate authority (one-time)](#1-assign-the-operators-roles).

```sh
ME=$(az ad signed-in-user show --query id -o tsv | tr -d '\r')
KV_ID=$(az keyvault show --name "$KEY_VAULT" --resource-group "$RG" --query id -o tsv | tr -d '\r')
MSYS_NO_PATHCONV=1 az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Key Vault Secrets Officer" --scope "$KV_ID" -o none
```

Allow a few minutes for the assignment to apply, then write both values. The secret is read
without echo, so it stays out of the terminal and the shell history:

```sh
read -r -p "Client ID: " GATEWAY_CLIENT_ID
az keyvault secret set --vault-name "$KEY_VAULT" --name gateway-auth-client-id \
  --value "$GATEWAY_CLIENT_ID" --query name -o tsv

read -rs -p "Client secret: " GATEWAY_CLIENT_SECRET && echo
az keyvault secret set --vault-name "$KEY_VAULT" --name gateway-auth-client-secret \
  --value "$GATEWAY_CLIENT_SECRET" --query name -o tsv
```

Keep the shell open for step 3, which uses both variables. Then remove the role and the
variables:

```sh
unset GATEWAY_CLIENT_ID GATEWAY_CLIENT_SECRET
MSYS_NO_PATHCONV=1 az role assignment delete --assignee "$ME" --role "Key Vault Secrets Officer" --scope "$KV_ID"
```

After the next infrastructure deployment, step 5 of [Deploy](#deploy) shows the two
`GatewayAuth__*` references as `Resolved`.

### 3. Check the credentials against the gateway mock

The Function cannot forward from staging until a reachable gateway exists
([#15](https://github.com/asnielsen789/IIoT-platform/issues/15)), so the credentials are checked
locally: a token from the identity provider, accepted by the gateway mock running against the
same tenant. In one terminal, start the mock
([`tools/IIoT.GatewayMock`](../tools/IIoT.GatewayMock/README.md#against-auth0)):

```sh
dotnet run --project tools/IIoT.GatewayMock -- \
  --GatewayMock:Jwt:Authority=https://<tenant domain>/ \
  --GatewayMock:Jwt:Issuer=https://<tenant domain>/ \
  --GatewayMock:Jwt:Audience=<the API identifier>
```

In the shell from step 2, get a token the way the Function does, and send the contract's
example batch with it:

```sh
TOKEN=$(curl -s https://<tenant domain>/oauth/token \
  --data-urlencode grant_type=client_credentials \
  --data-urlencode client_id="$GATEWAY_CLIENT_ID" \
  --data-urlencode client_secret="$GATEWAY_CLIENT_SECRET" \
  --data-urlencode audience=<the API identifier> | grep -o '"access_token":"[^"]*"' | cut -d'"' -f4)

BODY=docs/schemas/gateway/examples/valid-single-reading.json
KEY=$(grep -o '"messageId": *"[^"]*"' "$BODY" | cut -d'"' -f4)
CURL_SSL_BACKEND=schannel curl -s -o /dev/null -w "%{http_code}\n" https://localhost:7180/v1/readings \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $KEY" --data-binary @"$BODY"
unset TOKEN
```

The commands need nothing beyond Git Bash:
- **`grep` and `cut` read the JSON**, since `jq` is not part of Git Bash.
- **`CURL_SSL_BACKEND=schannel`** makes Git Bash's `curl` verify the mock's certificate against the
  Windows certificate store, which holds the trusted ASP.NET Core development certificate
  (`dotnet dev-certs https --trust`). Its default backend, OpenSSL, has its own CA bundle without
  that certificate and fails with exit code 60.

It worked when the mock answers `202`, and `401` without the `Authorization` header. The token is
a credential for as long as it lives: do not paste it anywhere, including into a token decoder
website.

### Rotating the client secret

Rotate in Auth0, write the new value as a new version of `gateway-auth-client-secret` (step 2),
then refresh the references (step 5 of [Deploy](#deploy)). The Function keeps its cached token
until shortly before it expires, so forwarding continues meanwhile, but a token renewal with the
old secret fails until the refresh.

## Device certificate authority (one-time)

Sets up the device CA of [ADR-009](../docs/adr/adr-009-device-pki.md) once its keys exist, and
issues the first device certificate. Steps 1 to 3 are once per CA: the keys survive every
infrastructure deployment ([Device certificate authority](#device-certificate-authority)), and so
do the certificates the tool stores next to them. Steps 4 and 5 are per device. The tool is
described in [`tools/IIoT.DeviceCa`](../tools/IIoT.DeviceCa/README.md), the device side in the
[test client's README](../tools/IIoT.TestClient/README.md#certificate-authentication).

> [!NOTE]
> First run against staging on 2026-10-25, the live test of
> [#11](https://github.com/asnielsen789/IIoT-platform/issues/11). Every step worked as described
> here, once two corrections from that run were made: Git Bash's path conversion (step 1) and the
> subject IoT Hub shows (step 3). What the run settled is at the end, in
> [What the first run settled](#what-the-first-run-settled).

Run from the repository root in Git Bash, signed in with `az login` as the operator, in the
shell where `RG`, `IOT_HUB` and `KEY_VAULT` are set (step 3 of [Deploy](#deploy)). The tool
signs in through the same Azure CLI session; if Visual Studio is signed in with another
account, `export AZURE_TOKEN_CREDENTIALS=azureclicredential` first, since Visual Studio comes
before the Azure CLI. Device files go to `~/.secrets/iiot`, outside the repository. The private
key never leaves that directory; everything else is public.

### 1. Assign the operator's roles

Every operation the tool performs is data-plane, authorised by Azure RBAC with your Microsoft
Entra sign-in. Four built-in roles cover it, each on the narrowest scope the operation allows:

| Role | Scope | Needed for |
|---|---|---|
| Key Vault Crypto User | Each of the keys `device-ca-root` and `device-ca-issuing` | Signing: `keys/read` and `keys/sign/action`. `init` signs with both keys, `issue` only with the issuing key |
| Key Vault Secrets Officer | The vault, until `init` has run | Creating the secrets `device-ca-root-certificate` and `device-ca-issuing-certificate` |
| Key Vault Secrets User | Each of those two secrets, after `init` | Reading them: `secrets/getSecret/action`, for `issue` and `ca-certificate` |
| IoT Hub Registry Contributor | The IoT hub | Looking up and registering devices: `IotHubs/devices/read` and `IotHubs/devices/write` |

The data actions are from Microsoft's
[built-in roles for security](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/security)
and for [Internet of Things](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/internet-of-things),
and from [Control access to IoT Hub with Microsoft Entra ID](https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-azure-ad#permissions-for-iot-hub-service-apis).

- **Why per key and per secret.** Microsoft recommends roles at vault scope and per-object
  assignments only as an exception
  ([Azure RBAC for Key Vault](https://learn.microsoft.com/azure/key-vault/general/rbac-guide#best-practices-for-individual-keys-secrets-and-certificates-role-assignments)).
  This vault is the exception it describes: it is shared, and also holds the Function's
  connection string `iothub-telemetry-connection`. At vault scope, the operator could read that
  secret and sign with any key the vault ever holds; on the CA's own objects, the roles reach
  nothing else.
- **Why Secrets Officer on the vault, and only for `init`.** The two certificate secrets do not
  exist until `init` creates them, so there is nothing narrower to assign it on. Remove it
  once `init` has run; reading the certificates afterwards needs only Secrets User on them.
- **The hub is the narrowest IoT Hub scope.** IoT Hub supports no role assignment below an
  individual hub. Registry Contributor also allows deleting devices; a custom role without
  `devices/delete`, like one with only `keys/read` and `keys/sign/action` instead of Crypto User
  (which can also update and back up the keys), is a candidate for the
  [security review (#21)](https://github.com/asnielsen789/IIoT-platform/issues/21).
- Assigning roles needs `Microsoft.Authorization/roleAssignments/write`, which Owner has
  ([Prerequisites](#prerequisites)).

```sh
ME=$(az ad signed-in-user show --query id -o tsv | tr -d '\r')
KV_ID=$(az keyvault show --name "$KEY_VAULT" --resource-group "$RG" --query id -o tsv | tr -d '\r')
HUB_ID=$(az iot hub show --name "$IOT_HUB" --resource-group "$RG" --query id -o tsv | tr -d '\r')

assign() {
  MSYS_NO_PATHCONV=1 az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
    --role "$1" --scope "$2" -o none
}

assign "Key Vault Crypto User" "$KV_ID/keys/device-ca-root"
assign "Key Vault Crypto User" "$KV_ID/keys/device-ca-issuing"
assign "Key Vault Secrets Officer" "$KV_ID"
assign "IoT Hub Registry Contributor" "$HUB_ID"

MSYS_NO_PATHCONV=1 az role assignment list --assignee "$ME" --all \
  --query "[?contains(scope, '$KEY_VAULT') || contains(scope, '$IOT_HUB')].roleDefinitionName" -o tsv
```

- **`MSYS_NO_PATHCONV=1`, on the role commands only.** A scope starts with `/subscriptions/`, and
  Git Bash rewrites an argument that looks like an absolute path before passing it to a Windows
  program, so `az` receives `C:/Program Files/Git/subscriptions/…` and fails with
  `MissingSubscription`. The variable turns that rewriting off. It is set per command because
  steps 3 to 5 pass file paths under `~`, which need the rewriting.
- **The list shows the result.** On the first run, `az role assignment create` printed nothing
  for a new assignment, so the last command lists the four roles instead: two Key Vault Crypto
  User, one Key Vault Secrets Officer and one IoT Hub Registry Contributor. The resource IDs
  contain the subscription ID, so they stay in variables and are never printed.
- Allow a few minutes before step 2: a new role assignment is not applied at once
  ([Azure RBAC for Key Vault](https://learn.microsoft.com/azure/key-vault/general/rbac-guide#test-and-verify)).

### 2. Create the CA certificates

```sh
dotnet run --project tools/IIoT.DeviceCa -- init --vault "$KEY_VAULT"
```

It worked when it prints `Stored the CA certificates in kv-iiot-staging-neu-xxxx` with the
subjects `CN=IIoT Device Root CA` and `CN=IIoT Device Issuing CA`, their validity and their
SHA-256 thumbprints. "Key Vault refused the request" means a role assignment from step 1 has not
applied yet: wait and run it again. If the vault holds CA certificates already, `init` changes
nothing and says so; go on to step 3, and use `--force` only to start the CA again (see
[`init`](../tools/IIoT.DeviceCa/README.md#init-create-the-ca-certificates)).

Then trade Secrets Officer on the vault for Secrets User on the two certificates:

```sh
MSYS_NO_PATHCONV=1 az role assignment delete --assignee "$ME" --role "Key Vault Secrets Officer" --scope "$KV_ID"
assign "Key Vault Secrets User" "$KV_ID/secrets/device-ca-root-certificate"
assign "Key Vault Secrets User" "$KV_ID/secrets/device-ca-issuing-certificate"
```

### 3. Register the issuing CA certificate in IoT Hub

Export the issuing CA certificate, the one IoT Hub authenticates devices against, and register it
as verified:

```sh
mkdir -p ~/.secrets/iiot
dotnet run --project tools/IIoT.DeviceCa -- ca-certificate --vault "$KEY_VAULT" \
  --out ~/.secrets/iiot/device-ca-issuing.pem

az iot hub certificate create --hub-name "$IOT_HUB" --resource-group "$RG" \
  --name device-ca-issuing --path ~/.secrets/iiot/device-ca-issuing.pem --verified \
  --query "{name:name, verified:properties.isVerified, subject:properties.subject, thumbprint:properties.thumbprint}" -o table
```

It worked when the table shows `device-ca-issuing`, `True`, `IIoT Device Issuing CA` (IoT Hub
shows the subject without `CN=`) and the SHA-1 thumbprint `ca-certificate` printed. The command and its `--verified` flag are documented in
the [Azure CLI reference](https://learn.microsoft.com/cli/azure/iot/hub/certificate#az-iot-hub-certificate-create);
`--verified` sets the certificate's status to verified on upload, as Microsoft's own walkthrough
does for a subordinate CA
([Create and upload certificates for testing](https://learn.microsoft.com/azure/iot-hub/tutorial-x509-test-certs#register-your-subordinate-ca-certificate-to-your-iot-hub)),
instead of answering a proof-of-possession challenge, which the tool does not implement.

- **Registered: the issuing CA, not the root.** The issuing CA signs the devices, so a device
  certificate chains to it directly. The root never appears in IoT Hub.
- **Control plane, not data plane.** Registering a CA certificate is an operation on the hub
  resource, `Microsoft.Devices/iotHubs/certificates/Write`, which Owner and Contributor include.
  An operator without them needs a role with that action on the hub; among the built-in roles,
  Azure Device Registry Contributor has it (`iothubs/certificates/*`, `iothubs/read` and the
  device data actions) without any other write on the hub
  ([built-in roles for Internet of Things](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/internet-of-things#azure-device-registry-contributor)).
- **Shared access keys stay on.** Registering a CA adds a way to authenticate; devices with keys,
  such as `tank-000001` from step 7 of [Deploy](#deploy), keep working. Turning device keys off
  belongs to the security review ([#21](https://github.com/asnielsen789/IIoT-platform/issues/21)).

### 4. Issue a device certificate

A new device, because `issue` never changes how an existing one authenticates, and `tank-000001`
uses a key. First its key and CSR, made by the test client as device firmware would, then the
certificate:

```sh
DEVICE=tank-000002
dotnet run --project tools/IIoT.TestClient -- create-csr --device-id "$DEVICE"

dotnet run --project tools/IIoT.DeviceCa -- issue --vault "$KEY_VAULT" --hub "$IOT_HUB" \
  --csr ~/.secrets/iiot/"$DEVICE".csr.pem --device-id "$DEVICE" --out ~/.secrets/iiot
```

`issue` writes `tank-000002.cert.pem` and `tank-000002.chain.pem` next to the key and registers
the device. Check the registration: expect `certificateAuthority` and `enabled`.

```sh
az iot hub device-identity show --hub-name "$IOT_HUB" --device-id "$DEVICE" \
  --query "{auth:authentication.type, status:status}" -o table
```

### 5. Connect the test client with it

Check the files without connecting, then send one valid message:

```sh
dotnet run --project tools/IIoT.TestClient -- --hub "$IOT_HUB" --device-id "$DEVICE" \
  --cert ~/.secrets/iiot/"$DEVICE".chain.pem --key ~/.secrets/iiot/"$DEVICE".key.pem --once --dry-run

dotnet run --project tools/IIoT.TestClient -- --hub "$IOT_HUB" --device-id "$DEVICE" \
  --cert ~/.secrets/iiot/"$DEVICE".chain.pem --key ~/.secrets/iiot/"$DEVICE".key.pem --once
```

The dry run prints the certificate (`CN=tank-000002`, issued by `CN=IIoT Device Issuing CA`).
The real run worked when it prints `connected: Success` and the message `-> Success`. Then
confirm the message as in step 7 of [Deploy](#deploy): one more message in
`d2c.telemetry.ingress.success` and none in `d2c.telemetry.egress.fallback`, a `Connections` log
entry for `tank-000002`, and `Device tank-000002 measured level=...` in the Function's log. A
`DEVICE_KEY` still exported from step 7 is ignored: `--cert` selects the certificate.

If it does not connect:

| Symptom | Likely cause |
|---|---|
| The client refuses before connecting, naming the certificate's CN | The `--cert` file is another device's; the CN must be the device ID |
| The TLS handshake fails | The issuing CA certificate is not registered or not verified (step 3). Sending the device certificate alone is not the cause: it was accepted on the first run |
| `connected: NotAuthorized`, or `MqttConnectingFailedException` with `NotAuthorized` or `BadUserNameOrPassword`, or the connection closes right after the handshake | The device is not registered as CA-authenticated, or is disabled (step 4). A disabled device gives exactly `connected: NotAuthorized` |

**Revoking a device** is disabling it, since IoT Hub does not check revocation lists
([Revoke a device certificate](https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-x509#revoke-a-device-certificate)).
The next connection is refused:

```sh
az iot hub device-identity update --hub-name "$IOT_HUB" --device-id "$DEVICE" --status disabled
```

### After the hub is recreated

Tearing down deletes the hub with its CA certificate registration and its devices. The vault
goes into soft delete; if it is recovered, the CA and every certificate it issued stay valid.
Then repeat step 3, and register each certificate device again, which `issue` will not do for a
device whose certificate files exist:

```sh
az iot hub device-identity create --hub-name "$IOT_HUB" --device-id tank-000002 --am x509_ca
```

If the vault was purged instead, the CA is gone: see [Tearing down](#tearing-down).

### What the first run settled

Three things nothing offline could settle. The first run, on 2026-10-25, answered all three,
and they are recorded in [#11](https://github.com/asnielsen789/IIoT-platform/issues/11):

1. **The device certificate alone is accepted.** The test client sends its certificate without
   the issuing CA ([why](../tools/IIoT.TestClient/README.md#what-goes-on-the-wire)). Microsoft's
   overview says the device "presents its full certificate chain and the IoT hub can validate it
   because it knows the X.509 CA"
   ([Authenticate with X.509 certificates](https://learn.microsoft.com/azure/iot-hub/authenticate-authorize-x509#types-of-certificate-authentication)).
   With the issuing CA registered, the chain up to the CA the hub knows is the device certificate
   alone, and the hub accepted it: `connected: Success`, and the `Connections` log records the
   connection with authentication type `CA-Signed`.
2. **The empty password is accepted.** The client sends the CONNECT's password field empty
   rather than leaving it out, and the hub raised no objection.
3. **The registration survives redeployment.** The CA certificate is a child resource of the hub
   that the templates do not declare. After an incremental infrastructure deployment,
   `az iot hub certificate list --hub-name "$IOT_HUB" --query "value[].name" -o tsv` still lists
   `device-ca-issuing`, verified, with the same thumbprint, and `tank-000002` still connects.

## Zero-touch provisioning (one-time)

Lets a device provision itself through the Device Provisioning Service
([#48](https://github.com/asnielsen789/IIoT-platform/issues/48)) instead of being registered by
`IIoT.DeviceCa issue`. The service itself comes from the templates
([Device Provisioning Service](#device-provisioning-service)). This procedure gives it the device
CA and an enrollment group for it, once per CA. Run it after
[Device certificate authority (one-time)](#device-certificate-authority-one-time), in the same
shell, signed in as the operator. `az iot dps` comes from the `azure-iot` CLI extension
(`az extension add --name azure-iot`). First run on 2026-10-27.

```sh
DPS=$(output provisioningServiceName)
ME=$(az ad signed-in-user show --query id -o tsv | tr -d '\r')
DPS_ID=$(az iot dps show --resource-group "$RG" --name "$DPS" --query id -o tsv | tr -d '\r')
```

### 1. The operator's role

The enrollment group lives in DPS's data plane. Manage it with your Microsoft Entra sign-in
(`--auth-type login`) and the built-in role **Device Provisioning Service Data Contributor** on the
instance, not with the instance's own `provisioningserviceowner` key. The role assignment needs
`MSYS_NO_PATHCONV=1`, since its scope starts with `/subscriptions/`:

```sh
MSYS_NO_PATHCONV=1 az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Device Provisioning Service Data Contributor" --scope "$DPS_ID" -o none
```

### 2. Register the issuing CA certificate in DPS

The same certificate, file and reasoning as for the IoT Hub in step 3 of the device CA procedure:
DPS validates a device's certificate against the CA it signed, so the issuing CA is registered,
not the root, and `--verified true` sets it as verified on upload. Registering a certificate is a
control-plane operation, which Owner and Contributor include. No `MSYS_NO_PATHCONV` here: the
path under `~` needs Git Bash's conversion.

```sh
az iot dps certificate create --dps-name "$DPS" --resource-group "$RG" \
  --name device-ca-issuing --path ~/.secrets/iiot/device-ca-issuing.pem --verified true \
  --query "{name:name, verified:properties.isVerified, subject:properties.subject, thumbprint:properties.thumbprint}" -o table
```

It worked when the table shows `device-ca-issuing`, `True`, `IIoT Device Issuing CA` and the same
SHA-1 thumbprint as the IoT Hub registration.

### 3. Create the enrollment group

One group for the issuing CA, with X.509 CA attestation: every device whose certificate the
issuing CA signed can provision itself. A new role assignment can take a few minutes to apply;
until it has, the command is refused, and running it again later is enough.

```sh
az iot dps enrollment-group create --dps-name "$DPS" --resource-group "$RG" --auth-type login \
  --enrollment-id device-ca-issuing --ca-name device-ca-issuing --provisioning-status enabled -o none

az iot dps enrollment-group show --dps-name "$DPS" --resource-group "$RG" --auth-type login \
  --enrollment-id device-ca-issuing \
  --query "{status:provisioningStatus, attestation:attestation.type, ca:attestation.x509.caReferences.primary, reprovision:reprovisionPolicy}" -o json
```

It worked when the group is `enabled`, with attestation `x509` and CA `device-ca-issuing`.

- **Allocation and hubs are left empty** (`null`), so the group inherits the instance's: `Hashed`
  across every linked hub, which is one hub today. A hub linked later takes part without changing
  the group.
- **Re-provisioning** is at its defaults: `updateHubAssignment: true` and `migrateDeviceData: true`.
  A device that provisions again may be moved to another hub, and its twin goes with it. That is
  the mechanism for moving devices between hubs (step 7 of #48).

A device then provisions itself: the test client's `provision` command does what device firmware
would ([Zero-touch provisioning](../tools/IIoT.TestClient/README.md#zero-touch-provisioning)).

### After DPS or the hub is recreated

The certificate registration and the enrollment group belong to the DPS instance: tearing it down
deletes both, and steps 2 and 3 are repeated. Assign the role again too (step 1): a new instance
is a new scope. A device that provisioned before keeps its identity in the hub until the hub
itself is recreated.

## Naming

Names follow the Cloud Adoption Framework abbreviations:
`<abbreviation>-<workload>-<environment>-<region>`, with workload `iiot` and region code `neu`
for North Europe. The four resources whose names are global DNS names also get a four-character
suffix, written `xxxx` below. It comes from `uniqueString()` over the subscription ID and the
environment name, so it is stable across redeployments and differs between subscriptions.

| Resource | Name in `staging` |
|---|---|
| Resource group | `rg-iiot-staging-neu` |
| Managed identity the app runs as | `id-iiot-staging-neu` |
| Managed identity the deploy workflow logs in with | `id-iiot-staging-neu-github`, with the federated credential `github-staging` |
| Log Analytics workspace | `log-iiot-staging-neu` |
| Application Insights | `appi-iiot-staging-neu` |
| Storage account | `stiiotstagingneuxxxx` (storage names allow no hyphens) |
| Key Vault | `kv-iiot-staging-neu-xxxx` |
| IoT Hub | `iot-iiot-staging-neu-xxxx` |
| Flex Consumption plan | `asp-iiot-staging-neu` |
| Function App | `func-iiot-staging-neu-xxxx` |
| Gateway mock's App Service plan | `asp-iiot-staging-sdc-gateway-mock` (in Sweden Central: [Gateway mock](#gateway-mock-1)) |
| Gateway mock | `app-iiot-staging-sdc-gateway-mock-xxxx` |
| Device Provisioning Service | `provs-iiot-staging-neu-xxxx` |

Key Vault names are limited to 24 characters, which is why the environment name is limited to
seven. The deploy workflow's identity takes the qualifier `-github` after the usual name, which
tells it apart from the app's. Every resource carries the tags `workload`, `environment` and
`managed-by: bicep`.

## Design notes

### Region: North Europe

West Europe offers everything this platform needs, but Azure currently closes it to tenants that
have no resources there yet ([region access policy](https://learn.microsoft.com/azure/azure-resource-manager/troubleshooting/error-region-access-policy)):
a what-if there fails with `RequestDisallowedByAzure`. Sweden Central does not offer IoT Hub.
North Europe offers IoT Hub and Flex Consumption with .NET 10, and keeps the platform in the EU.

The one exception is the gateway mock, which runs in Sweden Central (`gatewayMockLocation`); see
[Gateway mock](#gateway-mock-1) under these design notes.

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
The app comes after everything it uses; only the deploy workflow's identity, whose role is on
the app, comes later.

| Role | Scope | Needed for |
|---|---|---|
| Storage Blob Data Owner | Storage account | Host storage, the deployment package, Event Hubs trigger checkpoints |
| Storage Table Data Contributor | Storage account | Diagnostic events the host writes when the app cannot start |
| Storage Queue Data Contributor | Queue `forwarding-failed` | `ProcessTelemetry` adds dead-letter entries; the `RetryForwarding` trigger reads the queue's length, and receives, releases and deletes entries |
| Storage Queue Data Message Sender | Queue `forwarding-failed-poison` | Moving an entry to the poison queue, by the trigger after the last attempt or by `RetryForwarding` itself |
| Key Vault Secrets User | Key Vault | Resolving the Key Vault references: `IoTHubConnection` and the gateway credentials `GatewayAuth__ClientId` and `GatewayAuth__ClientSecret` |

Host storage is identity-based (`AzureWebJobsStorage__accountName`, `__credential`,
`__clientId`) and shared-key access to the storage account is disabled: no account key is used
anywhere. The dead-letter queues are reached the same way, through a connection of their own,
`DeadLetterStorage__queueServiceUri`, `__credential` and `__clientId`, with the same identity.

The queue roles are each on one queue, the narrowest scope Azure RBAC offers for queue data
([Authorize access to queues using Microsoft Entra ID](https://learn.microsoft.com/azure/storage/queues/authorize-access-azure-active-directory#resource-scope)),
so the identity can reach these two queues and no other. For a queue trigger, Microsoft lists
Storage Queue Data Reader and Storage Queue Data Message Processor, and for queue output Storage
Queue Data Contributor or Message Sender
([Grant permissions to an identity](https://learn.microsoft.com/azure/azure-functions/manage-connections?pivots=functions-auth-identity&tabs=bindings#grant-permissions-to-an-identity)).
On `forwarding-failed` the trigger's pair is not enough: after a failed attempt the trigger makes
the entry invisible until the next one with Update Message, which needs `messages/write`
([Permissions for calling data operations](https://learn.microsoft.com/rest/api/storageservices/authorize-with-azure-active-directory#permissions-for-queue-service-operations)),
and of the built-in queue roles only Contributor grants it. What Contributor adds beyond the need,
deleting and changing that one queue, is a candidate for a custom role in the
[security review (#21)](https://github.com/asnielsen789/IIoT-platform/issues/21). On the poison
queue the identity only ever adds an entry, so Message Sender suffices.

The deploy workflow logs in as a second user-assigned identity, `id-iiot-staging-neu-github`
([Continuous deployment](#continuous-deployment)). The two share no role: the app's identity has
none on the app itself, so the app cannot redeploy itself, and the workflow's identity has none
on storage, Key Vault or the IoT Hub.

| Role | Scope | Needed for |
|---|---|---|
| Website Contributor | Function App | Deploying the package, refreshing the Key Vault references, syncing the triggers and listing the functions |

Website Contributor on the app is the role Microsoft documents for deploying with
`Azure/functions-action`. Every call that reads or changes something is on the Function App or
its SCM site: the action reads the app (which tells it the plan is Flex Consumption), its app
settings and its publishing credentials, and posts the package to One Deploy; the workflow
refreshes the Key Vault references, syncs the triggers and lists the functions. Two lookups go
wider and need no role of their own, because Azure answers them with only what the caller may
read: the login lists the subscriptions the identity can see, and the action searches the
subscription's resources by name to find the app's resource group. No storage role: on Flex
Consumption the deployment service writes the package to the deployment container with the
app's own identity, and push deployments never write to the container directly. The role also
allows changing the app's settings, which a code deployment does not need; there is no narrower
built-in role, and a custom one is a candidate for the
[security review (#21)](https://github.com/asnielsen789/IIoT-platform/issues/21).

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

### Device certificate authority

Devices authenticate with X.509 certificates from the platform's own CA
([ADR-009](../docs/adr/adr-009-device-pki.md)). `modules/device-ca-keys.bicep` creates its two
keys in the vault: `device-ca-root` and `device-ca-issuing`, both EC P-256, usable only to sign
and verify, and not exportable. Key Vault is not a CA service; the signing tool
[`tools/IIoT.DeviceCa`](../tools/IIoT.DeviceCa/README.md) builds the certificates and has Key
Vault sign them, so the private keys never leave the vault.

- **Redeploying leaves the keys alone.** The resource type is backed by the ARM operation
  *Keys - Create If Not Exist*: it creates the first version of a key that does not exist, and
  for one that does, returns it "without any write operations being performed"; it "does not
  create subsequent versions, and does not update existing keys" (azure-rest-api-specs,
  `Microsoft.KeyVault` stable 2026-05-15, `Keys_CreateIfNotExist`). Unlike the IoT Hub policy
  keys, the CA keys survive every infrastructure deployment. The flip side is that a changed
  key property in the template has no effect on an existing key.
- **The template creates keys only.** The CA certificates are public data that the tool writes
  to the vault as the secrets `device-ca-root-certificate` and `device-ca-issuing-certificate`.
  No role in the template lets anything use the keys: the Function App's identity reads
  secrets, and an operator who signs gets the roles in
  [Device certificate authority (one-time)](#device-certificate-authority-one-time) assigned by
  hand.
- **Software-protected.** The standard SKU protects keys in software. Production would use
  HSM-backed keys (`EC-HSM` in a premium vault, or Managed HSM) and keep the root offline;
  ADR-009 documents this and does not build it.

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

The workspace has a **daily ingestion cap**, `logAnalyticsDailyQuotaGb` (50 MB in staging), as
a backstop against cost: a crash loop that logs an exception on every restart is the likeliest
way for this environment to run up a bill. When the cap is reached, collection stops for the
rest of the day, until a reset hour that Azure picks and the portal shows, and some excess is
still ingested and billed
([Set daily cap on Log Analytics workspace](https://learn.microsoft.com/azure/azure-monitor/logs/daily-cap)).
Application Insights is affected too, since its data goes to this workspace. An investigation
that needs more, such as the scale test
([#20](https://github.com/asnielsen789/IIoT-platform/issues/20)), raises the parameter for its
duration.

### Gateway mock

The Function's downstream gateway in staging, so that a message can be followed from the device
to a receiver ([#15](https://github.com/asnielsen789/IIoT-platform/issues/15)). It is a mock, not
a gateway: it checks the bearer token and the readings contract, and keeps what it accepts in
memory. An operator with a real downstream system drops the module and points
`GatewayV1__BaseAddress` at that system instead.

- **App Service, free tier (F1), Linux.** It costs nothing, serves HTTPS on its
  `azurewebsites.net` name with a platform certificate, and has the .NET 10 runtime built in, so
  the mock deploys as plain `dotnet publish` output. F1 has no Always On: the app unloads when
  idle, and the first request afterwards waits for it to start. The Function's retries cover that.
  It also has a daily CPU quota of 60 minutes, far more than a mock receiving a message every few
  hours uses. Repeated stops, starts and cold starts can use it up all the same. On 2026-10-27,
  during the dead-letter live test, the app went to `QuotaExceeded` and answered `403` from about
  08:20 UTC until the daily reset at 00:00 UTC. The usage API reports no limits for a Linux free
  plan, and the logs cannot be downloaded while the app is stopped, so what used the quota could
  not be seen. To the Function such an outage is an ordinary forwarding failure: dead-lettering
  covers about four hours, and an entry that runs out of attempts waits in the poison queue for a
  replay ([Dead-lettered messages](#dead-lettered-messages)). Check the state with
  `az webapp show --query "{state:state, usage:usageState}"`.
- **Sweden Central, not North Europe.** On 2026-10-26 this subscription's App Service quota for
  F1 was 0 in North Europe, Germany West Central and UK South, and the deployment's preflight
  failed with `SubscriptionIsOverQuotaForSku`; Sweden Central and France Central had quota. The
  mock is a separate component, so only the Function's calls to it cross regions, and it stays
  in the EU. Its names carry its own region (`sdc`). To bring it to North Europe once quota
  exists there (Quotas in the portal, App Service), set `gatewayMockLocation` to `northeurope`:
  the names change, so the next deployment creates a new app; publish to it and delete the old
  plan and app. A plan's quota can be checked without creating anything by validating a minimal
  template with `az deployment group validate`.
- **No Azure access.** No managed identity, no Key Vault, no role assignment: its configuration
  is the issuer and the audience it accepts tokens for, both public.
- **Basic-auth publishing off** (`scm` and `ftp`), so no publishing password exists to leak;
  `az webapp deploy` uses Microsoft Entra ID. HTTPS only, TLS 1.2 minimum, FTP disabled.
- **Its log stays on the app** (file system, three days), for `az webapp log tail`. Nothing goes
  to Application Insights, so the mock adds nothing to the workspace's ingestion or its daily cap.

### Dead-letter queues

Decided in [ADR-010](../docs/adr/adr-010-dead-letter-handling.md). `modules/storage.bicep` declares
the queue service and both queues, `forwarding-failed` and its poison queue
`forwarding-failed-poison`, in the host storage account; `modules/function-app.bicep` sets their
connection.

- **Only forwarding failures.** A message rejected for its payload or its identity would fail the
  same way on every attempt, and is already logged, so it is not dead-lettered.
- **Both queues are declared.** The trigger's poison move and the queue client create a queue only
  when it is missing, so with both in place no identity needs the right to create a queue, and
  nothing changes the account's layout at run time.
- **A connection of its own**, `DeadLetterStorage__queueServiceUri`, `__credential` and
  `__clientId`, although it points at the host storage account: moving the queues to another account
  changes these settings and the role assignments, not the code.
- **The schedule is the queue trigger's**, in `src/IIoT.Functions/host.json`: `maxDequeueCount` 8,
  and `visibilityTimeout` 30 minutes, which Microsoft defines as "the time interval between retries
  when processing of a message fails"
  ([host.json settings](https://learn.microsoft.com/azure/azure-functions/functions-bindings-storage-queue#host-json)).
  The first retry follows 30 minutes after the failure, the last about four hours after. For a live
  test, the app settings `AzureFunctionsJobHost__extensions__queues__visibilityTimeout` and
  `__maxDequeueCount` override `host.json`, and the code follows them; changing them restarts the
  app.
- **Entries never expire.** The service's default time-to-live is seven days; the platform writes
  `-1`, which means "does not expire" ([Put Message](https://learn.microsoft.com/rest/api/storageservices/put-message)).
  For the same reason `RetryForwarding` moves an entry to the poison queue itself on the last
  attempt: the runtime's own move writes it with the default and lets it expire.
- **One entry, one queue message.** A queue message is at most 64 KiB once encoded, and the trigger
  expects base64, so an entry may be 48 KiB. The largest message the contract describes is about
  ten kilobytes as an entry; a larger one, possible because the schema allows unknown fields, is
  refused and logged at Critical rather than truncated.
- **Missing settings do not stop the app.** Without the `DeadLetterStorage` settings,
  `RetryForwarding` fails indexing and is disabled, and every dead-letter write is logged at
  Critical with the setting named, while `ProcessTelemetry` goes on processing.
- **The queue listener is less forgiving than the Event Hubs one.** It backs off on a missing queue
  or a server error, but a `403`, or a token it cannot obtain, ends the host, which the platform then
  restarts. After the first deployment that creates the role assignments, expect host restarts
  until they have propagated, which Microsoft says can take up to 30 minutes; messages wait in IoT
  Hub meanwhile and are processed from the checkpoint. Locally the host stops for good: run Azurite
  and set `DeadLetterStorage` to `UseDevelopmentStorage=true` in `local.settings.json`, since
  `credential` set to `managedidentity` cannot obtain a token on a developer machine.
- **Cost.** Queue operations cost US$0.004 per 10,000 in North Europe (Azure Retail Prices API,
  Queues v2, Standard LRS). The trigger polls an empty queue at most once a minute while an instance
  runs, about 43,000 operations a month, so a few US cents at most.

### Device Provisioning Service

Zero-touch provisioning ([#48](https://github.com/asnielsen789/IIoT-platform/issues/48)), the
second stage of the registration decision in
[ADR-009](../docs/adr/adr-009-device-pki.md). A device needs only the global endpoint
`global.azure-devices-provisioning.net`, the ID scope (output `provisioningIdScope`) and its
certificate. DPS checks the certificate against the CA in an enrollment group, creates the
device's identity in the hub it assigns, and tells the device which hub that is.

- **One tier, billed per operation.** S1 costs 0.66 DKK per 1,000 operations in North Europe
  (Azure Retail Prices API, 2026-10-27): a registration, a status lookup, an enrollment change.
  There is no monthly or unit fee, so an idle instance costs nothing.
- **Linked by key, through the hub's own `dps-link` policy:** RegistryWrite and ServiceConnect,
  the two rights DPS needs to create device identities
  ([How to manage linked IoT hubs](https://learn.microsoft.com/azure/iot-dps/how-to-manage-linked-iot-hubs)).
  Linking by managed identity is in preview and needs IoT Hub Data Contributor on the hub, its
  whole data plane, so it is not used yet. The key is read with `listKeys()` during the deployment
  and written straight into the link. It is never a parameter or an output.
- **The link follows the key rotation.** Every deployment regenerates the hub's keys (ADR-007),
  and the same deployment writes the new `dps-link` key into the link, since the provisioning
  service comes after the hub. Between the two, a device that provisions gets an error and
  retries. The hub must keep shared access policies enabled for the link to work.
- **The classic host name.** The link's connection string names `<hub>.azure-devices.net`, which
  devices already use. A link made in the portal or with the CLI now defaults to the TLS 1.3
  device endpoint, `<hub>.device.azure-devices.net`, which is in preview.
- **Allocation:** `Hashed`, DPS's default. With one linked hub it decides nothing; with more,
  a device lands in the same hub again when it provisions again.

## What the first deployment confirmed

The open points of the first deployment, as observed in
[#9](https://github.com/asnielsen789/IIoT-platform/issues/9):

1. **Key Vault reference as a trigger connection: confirmed** (2026-10-15). On Flex
   Consumption the reference resolves (step 5), the trigger connects and processes messages
   (step 7), and the app scales out from zero: after four idle days, a message was processed
   about 11 s after IoT Hub enqueued it (2026-10-19).
2. **Policy keys on redeployment: confirmed** (2026-10-19). As ADR-007's update concluded from
   the IoT Hub API reference, the second infrastructure deployment generated a new key for the
   Function's policy (the key hash under [Re-deploying](#re-deploying) changed), and after the
   step 5 refresh the Function went on processing: a message sent afterwards was processed
   about 16 s later.
3. **Routing on the message body: confirmed** (2026-10-15). The test client's message took the
   `telemetry-v1` route, with no message in the fallback count, which confirms routing and the
   documented MQTT property bag together.
