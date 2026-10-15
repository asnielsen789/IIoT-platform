// Function App on the Flex Consumption plan (ADR-006: .NET 10 does not run on Linux Consumption).
//
// Modelled on Microsoft's Flex Consumption samples (Azure-Samples/azure-functions-flex-consumption-samples
// and Azure-Samples/functions-quickstart-dotnet-azd). Runtime, scale and deployment storage live
// in functionAppConfig; the classic settings these replace (FUNCTIONS_WORKER_RUNTIME,
// WEBSITE_RUN_FROM_PACKAGE, alwaysOn, ...) are deprecated on Flex and deliberately absent.

@description('Function App name: globally unique (<name>.azurewebsites.net). Kept within 32 characters, the length the host ID is derived from.')
@minLength(2)
@maxLength(32)
param name string

@description('Name of the Flex Consumption plan.')
param planName string

@description('Azure region.')
param location string

@description('Tags applied to the plan and the app.')
param tags { *: string }

@description('Resource ID of the user-assigned managed identity the app runs as.')
param identityId string

@description('Client ID of that identity, which tells the Functions host which identity to use.')
param identityClientId string

@description('Name of the host storage account (AzureWebJobsStorage).')
param storageAccountName string

@description('Blob service endpoint of the storage account, ending in a slash.')
param storageBlobEndpoint string

@description('Blob container the app package is deployed to.')
param deploymentContainerName string

@description('Name of the Application Insights resource.')
param applicationInsightsName string

@description('Versionless URI of the Key Vault secret holding the IoT Hub connection string.')
param iotHubConnectionSecretUri string

@description('Consumer group on the IoT Hub built-in endpoint that the trigger reads.')
param iotHubConsumerGroupName string

@description('Upper bound on on-demand instances. Caps runaway cost and the share of the regional Flex Consumption core quota the app can take.')
@minValue(1)
@maxValue(1000)
param maximumInstanceCount int

@description('Memory per instance in MB. 2048 (one core) is Microsoft\'s recommended default.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    // Linux. Flex Consumption is Linux only.
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2025-03-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // Key Vault references resolve with the user-assigned identity. Without this they would use
    // a system-assigned identity, which this app does not have.
    keyVaultReferenceIdentity: identityId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageBlobEndpoint}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityId
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
        // No always-ready instances: they bill continuously, whether or not anything runs.
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        // Identity-based host storage: account name plus the managed identity, no account key.
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storageAccountName
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: identityClientId
        }
        // Not a secret per Microsoft, so a plain setting rather than a Key Vault reference; that
        // also keeps the portal's monitoring views working.
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsights.properties.ConnectionString
        }
        // The IoT Hub trigger's connection (Connection = "IoTHubConnection" in TelemetryFunction).
        // A Key Vault reference, resolved with the managed identity (ADR-007). The URI is
        // versionless, so a new secret version is used without changing this setting, once the
        // platform re-reads the reference (within 24 hours, or at once on a configuration change).
        {
          name: 'IoTHubConnection'
          value: '@Microsoft.KeyVault(SecretUri=${iotHubConnectionSecretUri})'
        }
        // The trigger's consumer group (ConsumerGroup = "%IoTHubConsumerGroup%"): the Function's
        // own, created in the IoT Hub module, so it does not compete with other readers of
        // `$Default`. Not a secret.
        {
          name: 'IoTHubConsumerGroup'
          value: iotHubConsumerGroupName
        }
      ]
    }
  }
}

@description('Name of the Function App.')
output name string = functionApp.name

@description('Resource ID of the Function App.')
output id string = functionApp.id
