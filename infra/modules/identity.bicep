// User-assigned managed identity for the Function App.
//
// User-assigned rather than system-assigned so that the identity, and every role assignment
// it needs (storage, Key Vault), exists before the Function App is created. A system-assigned
// identity only comes into being with the app, so the app could start before it is allowed to
// read its host storage or resolve its Key Vault reference. Microsoft's Flex Consumption
// quickstart for .NET (Azure-Samples/functions-quickstart-dotnet-azd) uses the same pattern.

@description('Name of the user-assigned managed identity.')
param name string

@description('Azure region.')
param location string

@description('Tags applied to the identity.')
param tags { *: string }

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: name
  location: location
  tags: tags
}

@description('Resource ID, used to attach the identity to the Function App.')
output id string = identity.id

@description('Client ID, used by the Functions host to select this identity.')
output clientId string = identity.properties.clientId
