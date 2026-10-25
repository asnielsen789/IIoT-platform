# Infrastructure

Bicep templates for the platform's Azure resources: IoT Hub with its message routing, Key Vault,
the Function App on a Flex Consumption plan with its storage account, and monitoring.

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
| `modules/storage.bicep` | Host storage and deployment package container, with the identity's storage roles |
| `modules/key-vault.bicep` | Key Vault (Azure RBAC), with the identity's read access to secrets |
| `modules/device-ca-keys.bicep` | The device certificate authority's root and issuing CA keys, in that vault ([ADR-009](../docs/adr/adr-009-device-pki.md)) |
| `modules/iot-hub.bicep` | IoT Hub: shared access policies, message routing, the Function's consumer group, resource logs |
| `modules/iot-hub-connection-secret.bicep` | Builds the Function's IoT Hub connection string and writes it to Key Vault |
| `modules/function-app.bicep` | Flex Consumption plan and Function App |
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
requests or commits. On a subscription where nothing is deployed yet, the plan is 22 resources
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

The first time, expect **22 resources to create**: the resource group; the IoT Hub, its consumer
group `telemetry-function` and its diagnostic setting `routes-and-connections`; the Log
Analytics workspace and Application Insights; the Key Vault with its role assignment, the
secret `iothub-telemetry-connection` and the device CA keys `device-ca-root` and
`device-ca-issuing`; the managed identity; the storage account with its blob
service, container `app-package` and two role assignments; the Flex Consumption plan and the
Function App; the deployment identity `id-iiot-staging-neu-github`, its federated credential
`github-staging` and its role assignment on the Function App. In the IoT Hub's properties, check
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
(`workspaceCapping`) and nothing else ([Monitoring](#monitoring)). On
later deployments the keys stay as they are: Azure creates a key only if none exists by that
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
  connection string, or recover it. **Purging also destroys the device CA keys.** The next
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
| Key Vault Secrets User | Key Vault | Resolving the `IoTHubConnection` Key Vault reference |

Host storage is identity-based (`AzureWebJobsStorage__accountName`, `__credential`,
`__clientId`) and shared-key access to the storage account is disabled: no account key is used
anywhere.

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
