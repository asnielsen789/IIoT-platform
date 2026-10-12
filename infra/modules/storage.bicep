// Storage account for the Function App: host storage (AzureWebJobsStorage) and the blob
// container Flex Consumption deploys the app package from.
//
// Both are reached with the Function's managed identity, so shared-key access is disabled, as
// in Microsoft's Flex Consumption samples (Azure-Samples/azure-functions-flex-consumption-samples).
// No account key is used anywhere in this deployment.

@description('Storage account name: 3-24 lowercase letters and digits.')
@minLength(3)
@maxLength(24)
param name string

@description('Azure region.')
param location string

@description('Tags applied to the storage account.')
param tags { *: string }

@description('Blob container that holds the Function App deployment package.')
param deploymentContainerName string

@description('Name of the Function App managed identity, in this resource group.')
param functionIdentityName string

// Built-in role definition IDs.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource functionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: functionIdentityName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2026-06-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    // With shared keys disabled, make the portal use Entra ID by default as well.
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // No virtual network in scope; the samples use the same open configuration without one.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-06-01' = {
  parent: storageAccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-06-01' = {
  parent: blobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Role assignment names are seeded with the identity's resource ID, which is known before
// deployment, rather than its principal ID, which is not. That keeps them deterministic and
// lets what-if evaluate this module instead of skipping it.

// Storage Blob Data Owner is the documented minimum for the host's AzureWebJobsStorage
// connection. It also covers the deployment container and the Event Hubs trigger's
// checkpoints, which the host keeps in blobs.
resource blobDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionIdentity.id, storageBlobDataOwnerRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Table access lets the host persist the diagnostic events it raises when the app cannot
// start. Microsoft documents it as an addition to Blob Data Owner for AzureWebJobsStorage.
// No queue role: queues are only needed by Durable Functions and blob triggers, which this
// app does not use.
resource tableDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionIdentity.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Name of the storage account.')
output name string = storageAccount.name

@description('Blob service endpoint, ending in a slash.')
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob

@description('Name of the deployment package container.')
output deploymentContainerName string = deploymentContainer.name
