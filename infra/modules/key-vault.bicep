// Key Vault holding the secrets the platform cannot avoid: today, the IoT Hub connection string
// the Function reads telemetry with (ADR-007).

@description('Key Vault name: 3-24 characters.')
@minLength(3)
@maxLength(24)
param name string

@description('Azure region.')
param location string

@description('Tags applied to the vault.')
param tags { *: string }

@description('Name of the Function App managed identity, in this resource group. It is granted read access to secrets.')
param functionIdentityName string

// Built-in role definition ID.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource functionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: functionIdentityName
}

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    // Azure RBAC, not access policies: access is granted per identity and per vault through
    // the same role assignments as everything else, and is visible in one place.
    enableRbacAuthorization: true
    // Soft delete stays at its default (enabled, 90 days).
    //
    // Purge protection is deliberately left off (the property is omitted: it cannot be set to
    // false explicitly, and once enabled it can never be disabled). This is a non-production
    // environment that gets torn down and recreated; with purge protection on, a deleted vault
    // could not be purged, and its name, which is deterministic, would stay blocked for the
    // whole retention period. A production environment would enable it.
    //
    // Public network access: no virtual network is in scope, and the platform resolves the
    // Function's Key Vault reference over the public endpoint. Access is controlled by RBAC.
    publicNetworkAccess: 'Enabled'
  }
}

// Read-only access to secret values, scoped to this vault alone. The name is seeded with the
// identity's resource ID, which is known before deployment, so what-if can evaluate it.
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Name of the vault.')
output name string = keyVault.name

@description('Resource ID of the vault.')
output id string = keyVault.id
