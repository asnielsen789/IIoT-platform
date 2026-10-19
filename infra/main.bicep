// Entry point for the platform's Azure infrastructure (#2).
//
// Subscription-scoped: it creates the resource group and deploys every module into it, so one
// `az deployment sub what-if` shows the whole plan. See README.md for usage and design notes.
targetScope = 'subscription'

@description('Environment name, used in resource names and tags. Lowercase letters and digits; at most 7 characters, which keeps the Key Vault name within its 24-character limit.')
@minLength(2)
@maxLength(7)
param environmentName string

@description('Azure region for every resource. Only regions that have been checked for IoT Hub and Flex Consumption support are allowed; each needs an entry in regionAbbreviations.')
@allowed([
  'northeurope'
])
param location string

@description('IoT Hub SKU. F1 is free but limited to one per subscription, and cannot be upgraded to B or S in place: leaving F1 means creating a new hub. Basic tiers are not offered: they lack cloud-to-device messaging, which the planned device configuration (#16) depends on.')
@allowed([
  'F1'
  'S1'
  'S2'
  'S3'
])
param iotHubSkuName string = 'F1'

@description('Upper bound on Function App instances. Caps runaway cost; at 2048 MB each instance also takes one core of the regional Flex Consumption quota.')
@minValue(1)
@maxValue(1000)
param functionAppMaximumInstanceCount int = 10

@description('GitHub OIDC subject prefix of the repository whose workflows deploy the Function\'s code (#5), exactly as GitHub reports it: `gh api repos/OWNER/REPO/actions/oidc/customization/sub --jq .sub_claim_prefix`. Repositories created after 15 July 2026 use the immutable form `repo:OWNER@OWNER-ID/REPO@REPO-ID`, older ones `repo:OWNER/REPO`.')
@minLength(6)
param githubOidcSubjectPrefix string

var workload = 'iiot'

// Region short codes for resource names. To add a region, check that it offers both IoT Hub
// and Flex Consumption (`az functionapp list-flexconsumption-locations`), then add it here and
// to the allowed values of `location`. West Europe offers both but is closed to tenants without
// existing resources there (https://aka.ms/locationineligible), so it is not listed.
var regionAbbreviations = {
  northeurope: 'neu'
}

// <workload>-<environment>-<region>, for example iiot-staging-neu.
var nameStem = toLower('${workload}-${environmentName}-${regionAbbreviations[location]}')

// Four of the resources have globally unique DNS names (IoT Hub, Function App, Key Vault,
// storage account). They get a short suffix that is deterministic per subscription and
// environment, so redeploying finds the same resources. Four characters keep the Key Vault
// name within 24.
var uniqueSuffix = take(uniqueString(subscription().subscriptionId, environmentName), 4)

// Every name is fixed here, before deployment, from Cloud Adoption Framework abbreviations.
// Modules receive names rather than other modules' outputs wherever a name identifies a
// resource, so that what-if can evaluate the whole plan.
var names = {
  resourceGroup: 'rg-${nameStem}'
  identity: 'id-${nameStem}'
  githubIdentity: 'id-${nameStem}-github'
  logAnalytics: 'log-${nameStem}'
  applicationInsights: 'appi-${nameStem}'
  storageAccount: 'st${replace(nameStem, '-', '')}${uniqueSuffix}'
  keyVault: 'kv-${nameStem}-${uniqueSuffix}'
  iotHub: 'iot-${nameStem}-${uniqueSuffix}'
  functionPlan: 'asp-${nameStem}'
  functionApp: 'func-${nameStem}-${uniqueSuffix}'
}

var tags = {
  workload: workload
  environment: environmentName
  'managed-by': 'bicep'
}

// The Function's dedicated, ServiceConnect-only IoT Hub policy, and the Key Vault secret that
// holds the connection string built from it (ADR-007).
var telemetryReaderPolicyName = 'function-telemetry-reader'
var telemetryConnectionSecretName = 'iothub-telemetry-connection'

// The Function's own consumer group on the hub's built-in endpoint (#9). Created by the IoT Hub
// module and handed to the Function App as the IoTHubConsumerGroup setting.
var telemetryConsumerGroupName = 'telemetry-function'

resource rg 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: names.resourceGroup
  location: location
  tags: tags
}

module identity 'modules/identity.bicep' = {
  scope: rg
  params: {
    name: names.identity
    location: location
    tags: tags
  }
}

module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  params: {
    logAnalyticsName: names.logAnalytics
    applicationInsightsName: names.applicationInsights
    location: location
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  params: {
    name: names.storageAccount
    location: location
    tags: tags
    // The account serves this one app, so a fixed container name is enough.
    deploymentContainerName: 'app-package'
    functionIdentityName: names.identity
  }
  dependsOn: [
    identity
  ]
}

module keyVault 'modules/key-vault.bicep' = {
  scope: rg
  params: {
    name: names.keyVault
    location: location
    tags: tags
    functionIdentityName: names.identity
  }
  dependsOn: [
    identity
  ]
}

module iotHub 'modules/iot-hub.bicep' = {
  scope: rg
  params: {
    name: names.iotHub
    location: location
    tags: tags
    skuName: iotHubSkuName
    telemetryReaderPolicyName: telemetryReaderPolicyName
    telemetryConsumerGroupName: telemetryConsumerGroupName
    logAnalyticsWorkspaceName: names.logAnalytics
  }
  // The hub's diagnostic setting writes to the workspace.
  dependsOn: [
    monitoring
  ]
}

module iotHubConnectionSecret 'modules/iot-hub-connection-secret.bicep' = {
  scope: rg
  params: {
    iotHubName: names.iotHub
    policyName: telemetryReaderPolicyName
    keyVaultName: names.keyVault
    secretName: telemetryConnectionSecretName
    tags: tags
  }
  dependsOn: [
    iotHub
    keyVault
  ]
}

// Created after everything it uses; only the deploy workflow's identity, whose role is on the
// app, comes later. Through the module outputs it consumes and the explicit dependency on
// monitoring, the app comes into being only after its identity, its role assignments (in the
// storage and Key Vault modules) and the secret it references all exist. The secret module
// depends on the IoT Hub module, so the consumer group the trigger reads exists too.
module functionApp 'modules/function-app.bicep' = {
  scope: rg
  params: {
    name: names.functionApp
    planName: names.functionPlan
    location: location
    tags: tags
    identityId: identity.outputs.id
    identityClientId: identity.outputs.clientId
    storageAccountName: names.storageAccount
    storageBlobEndpoint: storage.outputs.blobEndpoint
    deploymentContainerName: storage.outputs.deploymentContainerName
    applicationInsightsName: names.applicationInsights
    iotHubConnectionSecretUri: iotHubConnectionSecret.outputs.secretUri
    iotHubConsumerGroupName: telemetryConsumerGroupName
    maximumInstanceCount: functionAppMaximumInstanceCount
  }
  dependsOn: [
    monitoring
  ]
}

// The identity the deploy workflow (.github/workflows/deploy.yml) logs in with, through OIDC
// (#5). Its role assignment is on the Function App, so it comes after the app. The GitHub
// environment it trusts carries the environment's name: `staging` deploys from the GitHub
// environment `staging`.
module githubDeployment 'modules/github-deployment.bicep' = {
  scope: rg
  params: {
    name: names.githubIdentity
    location: location
    tags: tags
    githubSubject: '${githubOidcSubjectPrefix}:environment:${environmentName}'
    credentialName: 'github-${environmentName}'
    functionAppName: names.functionApp
  }
  dependsOn: [
    functionApp
  ]
}

@description('Resource group holding every resource.')
output resourceGroupName string = rg.name

@description('Function App name, for deploying the app package.')
output functionAppName string = functionApp.outputs.name

@description('IoT Hub name.')
output iotHubName string = iotHub.outputs.name

@description('IoT Hub host name devices connect to.')
output iotHubHostName string = iotHub.outputs.hostName

@description('Key Vault name.')
output keyVaultName string = keyVault.outputs.name

@description('Storage account name.')
output storageAccountName string = storage.outputs.name

@description('Application Insights name.')
output applicationInsightsName string = monitoring.outputs.applicationInsightsName

@description('Client ID of the identity the deploy workflow logs in with: the value of the GitHub environment variable AZURE_CLIENT_ID. An identifier, not a secret.')
output githubDeploymentClientId string = githubDeployment.outputs.clientId
