// Storage account for the Function App: host storage (AzureWebJobsStorage), the blob container
// Flex Consumption deploys the app package from, and the dead-letter queues (ADR-010).
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

@description('Queue for messages whose forwarding failed, which the RetryForwarding function reads (ADR-010). Its poison queue is this name with -poison appended, the queue trigger\'s convention.')
param forwardingFailedQueueName string

// Built-in role definition IDs.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageQueueDataMessageSenderRoleId = 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a'

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
// The host itself needs no queue role here: queues are only needed by Durable Functions and blob
// triggers, which this app does not use. The dead-letter queues below have roles of their own,
// on each queue.
resource tableDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionIdentity.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The dead-letter queues (ADR-010). Both are declared, the poison queue included, so that nothing
// creates a queue at run time: the trigger's poison move and the queue client only create a queue
// that is missing, so with both in place no identity needs the right to create one.
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2026-06-01' = {
  parent: storageAccount
  name: 'default'
}

resource forwardingFailedQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-06-01' = {
  parent: queueService
  name: forwardingFailedQueueName
}

resource forwardingFailedPoisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-06-01' = {
  parent: queueService
  name: '${forwardingFailedQueueName}-poison'
}

// The queue roles are on the individual queues, the narrowest scope Azure RBAC offers for queue
// data, so the identity can touch these two queues and no other.
//
// On forwarding-failed, Storage Queue Data Contributor. The IoT Hub function adds entries (Put
// Message), and RetryForwarding's trigger checks the queue and reads its length (Get Queue
// Metadata), receives and deletes entries (Get Messages, Delete Message), and on a failed attempt
// sets the entry's visibility for the next one (Update Message, which needs messages/write).
// Microsoft lists Storage Queue Data Reader plus Message Processor for a queue trigger, but neither
// grants messages/write, so the trigger could not release a failed entry for its next attempt.
// Contributor is the only built-in queue role that does; what it adds beyond the need, deleting
// and changing this one queue, is a candidate for a custom role in the security review (#21).
resource forwardingFailedContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(forwardingFailedQueue.id, functionIdentity.id, storageQueueDataContributorRoleId)
  scope: forwardingFailedQueue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// On forwarding-failed-poison, Storage Queue Data Message Sender: add only. The trigger's own move
// after the last attempt and RetryForwarding's explicit moves both just add the entry. Nothing in
// the app reads the poison queue; an operator does, with roles of their own (README.md).
resource forwardingFailedPoisonSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(forwardingFailedPoisonQueue.id, functionIdentity.id, storageQueueDataMessageSenderRoleId)
  scope: forwardingFailedPoisonQueue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataMessageSenderRoleId)
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

@description('Queue service endpoint, without a trailing slash, in the form Microsoft documents for an identity-based queue connection (queueServiceUri).')
output queueServiceUri string = 'https://${storageAccount.name}.queue.${environment().suffixes.storage}'

@description('Name of the queue for messages whose forwarding failed.')
output forwardingFailedQueueName string = forwardingFailedQueue.name
