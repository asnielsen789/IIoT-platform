// Builds the Event Hubs-compatible connection string for the Function's dedicated IoT Hub
// policy and writes it straight into Key Vault (ADR-007).
//
// The key is read with listKeys() while the deployment runs and goes directly into the secret.
// It is never a parameter or an output, so it appears in no parameter file, deployment history
// or log, and no person handles it. Only the secret's URI leaves this module.

@description('Name of the IoT Hub whose built-in endpoint the Function reads.')
param iotHubName string

@description('Shared access policy whose key the connection string carries. Must hold ServiceConnect.')
param policyName string

@description('Name of the Key Vault to store the connection string in.')
param keyVaultName string

@description('Name of the Key Vault secret.')
param secretName string

@description('Tags applied to the secret.')
param tags { *: string }

resource iotHub 'Microsoft.Devices/IotHubs@2023-06-30' existing = {
  name: iotHubName
}

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: keyVaultName
}

// The built-in endpoint: an sb:// address ending in a slash, and the Event Hubs-compatible name.
var eventsEndpoint = iotHub.properties.eventHubEndpoints.events

// listKeys() returns every policy on the hub; only the Function's own policy is used.
var telemetryReaderKey = filter(iotHub.listKeys().value, rule => rule.keyName == policyName)[0].primaryKey

resource connectionSecret 'Microsoft.KeyVault/vaults/secrets@2026-05-15' = {
  parent: keyVault
  name: secretName
  tags: tags
  properties: {
    contentType: 'Event Hubs-compatible connection string for IoT Hub policy ${policyName}'
    // EntityPath carries the Event Hubs-compatible name. A name in the connection string
    // overrides the trigger attribute's event hub name at runtime, so the existing
    // [EventHubTrigger("messages/events", ...)] needs no change.
    value: 'Endpoint=${eventsEndpoint.endpoint};SharedAccessKeyName=${policyName};SharedAccessKey=${telemetryReaderKey};EntityPath=${eventsEndpoint.path}'
  }
}

@description('Versionless URI of the secret, for a Key Vault reference that follows the latest version.')
output secretUri string = connectionSecret.properties.secretUri
