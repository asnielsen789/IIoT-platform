// The downstream gateway mock (tools/IIoT.GatewayMock), hosted so that the Function in Azure has
// a gateway to forward to (#15). A development tool, not a gateway: it validates the readings
// contract and the bearer token, and keeps what it accepts in an archive of its own (ADR-012).
//
// App Service on the free tier (F1), Linux: no cost, HTTPS on its azurewebsites.net name with a
// platform certificate, and the .NET 10 runtime built in, so the mock is deployed as a plain
// `dotnet publish` output (infra/README.md, "Gateway mock"). F1 has no Always On: the app
// unloads when idle, and the first request after that waits for it to start. That suits a mock
// that receives a message every few hours. It also has a daily CPU quota of 60 minutes.
//
// The archive is a storage account of the mock's own, next to it, not a container in the
// platform's: the Function's identity is Storage Blob Data Owner on the platform's account, and the
// platform stores no readings (ADR-005, ADR-008), so it must not be able to read the receiver's.
// An operator who drops this module drops the receiver's data with it.
//
// The app has a system-assigned identity, for two things: a data role on the archive's one
// container, and, with the optional dashboard (#47) on, read access to exactly two Key Vault
// secrets, the dashboard's client ID and secret at the identity provider, through Key Vault
// references. The API's own configuration is three public values, the issuer and audience it
// accepts tokens for.

@description('Name of the App Service plan.')
param planName string

@description('Name of the web app. Globally unique: it is the first label of its azurewebsites.net host name.')
param name string

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags { *: string }

@description('OpenID Connect issuer whose tokens the mock accepts: the Function\'s identity provider, with the trailing slash its tokens carry in `iss`. Its discovery document supplies the signing keys.')
param jwtIssuer string

@description('Audience the mock requires in `aud`: the gateway API\'s identifier, the same one the Function requests its token for.')
param jwtAudience string

@description('OpenID Connect issuer people sign in to the dashboard with (GatewayMock:Dashboard:Authority), with its trailing slash. Empty leaves the dashboard off.')
param dashboardAuthority string = ''

@description('Key Vault in this resource group that holds the dashboard\'s client ID and secret. Read only when the dashboard is on.')
param keyVaultName string

@description('Name of the secret holding the dashboard\'s client ID at the identity provider. It must exist before a deployment that turns the dashboard on.')
param dashboardClientIdSecretName string

@description('Name of the secret holding the dashboard\'s client secret. It must exist before a deployment that turns the dashboard on.')
param dashboardClientSecretSecretName string

@description('Name of the archive\'s storage account (ADR-012): 3-24 lowercase letters and digits, globally unique.')
@minLength(3)
@maxLength(24)
param archiveStorageAccountName string

@description('Days the archive keeps a batch. A lifecycle rule deletes blobs older than this, about once a day.')
@minValue(1)
param archiveRetentionDays int

var dashboard = !empty(dashboardAuthority)

// The archive's one container. The mock reads the name from its setting, not from code.
var archiveContainerName = 'readings'

// Built-in role definition IDs.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// The archive (ADR-012): one JSON blob per accepted batch, the request body as it arrived. The
// same settings as the platform's account: Entra ID only, no shared keys, no public blobs, TLS 1.2.
resource archiveStorageAccount 'Microsoft.Storage/storageAccounts@2026-06-01' = {
  name: archiveStorageAccountName
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
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource archiveBlobService 'Microsoft.Storage/storageAccounts/blobServices@2026-06-01' = {
  parent: archiveStorageAccount
  name: 'default'
}

// Declared here, so that the mock never needs the right to create a container.
resource archiveContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-06-01' = {
  parent: archiveBlobService
  name: archiveContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Retention. The service runs the rule about once a day, so a blob can outlive the period by up to
// a day. Deleting costs nothing
// (https://learn.microsoft.com/azure/storage/blobs/lifecycle-management-overview).
resource archiveRetention 'Microsoft.Storage/storageAccounts/managementPolicies@2026-06-01' = {
  parent: archiveStorageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'retention'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                '${archiveContainer.name}/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterCreationGreaterThan: archiveRetentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: keyVaultName
}

// Written by an operator (infra/README.md, "Gateway mock dashboard (one-time)"), never by the
// template, which would need the values as parameters.
resource dashboardClientIdSecret 'Microsoft.KeyVault/vaults/secrets@2026-05-15' existing = {
  parent: keyVault
  name: dashboardClientIdSecretName
}

resource dashboardClientSecretSecret 'Microsoft.KeyVault/vaults/secrets@2026-05-15' existing = {
  parent: keyVault
  name: dashboardClientSecretSecretName
}

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    // `reserved` is what makes an App Service plan a Linux plan.
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2025-03-01' = {
  name: name
  location: location
  tags: tags
  kind: 'app,linux'
  // System-assigned: it exists only for this app's archive and Key Vault references, and goes
  // with the app.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      // Not available on F1; stated so that nobody expects the app to stay loaded.
      alwaysOn: false
      appSettings: concat(
        [
          // GatewayJwtOptions (tools/IIoT.GatewayMock/Authentication): the mock refuses to start
          // without an issuer and an audience. For Auth0 the authority is the issuer itself.
          {
            name: 'GatewayMock__Jwt__Authority'
            value: jwtIssuer
          }
          {
            name: 'GatewayMock__Jwt__Issuer'
            value: jwtIssuer
          }
          {
            name: 'GatewayMock__Jwt__Audience'
            value: jwtAudience
          }
          // ReadingArchiveOptions (tools/IIoT.GatewayMock/Readings): the container switches the
          // archive on; the app reaches it with its own identity, never a key or a SAS token.
          {
            name: 'GatewayMock__Archive__ContainerUri'
            value: '${archiveStorageAccount.properties.primaryEndpoints.blob}${archiveContainer.name}'
          }
          {
            name: 'GatewayMock__Archive__Credential'
            value: 'managedidentity'
          }
        ],
        dashboard
          ? [
              // DashboardOptions (tools/IIoT.GatewayMock/Dashboard). The authority switches the
              // dashboard on; the client ID and secret are Key Vault references, resolved with
              // the app's own identity.
              {
                name: 'GatewayMock__Dashboard__Authority'
                value: dashboardAuthority
              }
              {
                name: 'GatewayMock__Dashboard__ClientId'
                value: '@Microsoft.KeyVault(SecretUri=${keyVault.properties.vaultUri}secrets/${dashboardClientIdSecretName})'
              }
              {
                name: 'GatewayMock__Dashboard__ClientSecret'
                value: '@Microsoft.KeyVault(SecretUri=${keyVault.properties.vaultUri}secrets/${dashboardClientSecretSecretName})'
              }
              // TLS ends at App Service's front end. On Linux it reaches the app's container from
              // the link-local range, and its X-Forwarded-Proto is believed from there only, so
              // the sign-in's redirect URI is https://. Link-local addresses are not routed, so
              // nothing outside the host can connect from them. Not the private ranges of
              // Microsoft's sample: with those, staging still sent http:// (2026-10-30;
              // tools/IIoT.GatewayMock/README.md, "Behind a TLS-terminating proxy").
              {
                name: 'GatewayMock__ForwardedHeaders__KnownNetworks__0'
                value: '169.254.0.0/16'
              }
            ]
          : []
      )
    }
  }
}

// The archive: read, list and create blobs in its one container. Storage Blob Data Contributor is
// the narrowest built-in role that can write a blob; it also allows deleting and overwriting them,
// which the mock never does (its writes are create-only, ADR-012). A custom role without delete is
// a candidate for the security review (#21). Until the assignment has propagated, minutes after a
// first deployment, a write is refused: the mock answers 503 and the Function delivers again
// (ADR-010).
resource archiveBlobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(archiveContainer.id, app.id, storageBlobDataContributorRoleId)
  scope: archiveContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Read access to the dashboard's two secrets, each scoped to the secret itself, not the vault:
// the vault also holds the Function's IoT Hub connection string and its gateway credentials,
// which the mock has no business reading.
resource clientIdSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (dashboard) {
  name: guid(dashboardClientIdSecret.id, app.id, keyVaultSecretsUserRoleId)
  scope: dashboardClientIdSecret
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource clientSecretSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (dashboard) {
  name: guid(dashboardClientSecretSecret.id, app.id, keyVaultSecretsUserRoleId)
  scope: dashboardClientSecretSecret
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Deployment authenticates with Microsoft Entra ID only: no FTP and no basic-auth publishing
// credentials exist to leak. `az webapp deploy` uses the operator's Azure CLI sign-in.
resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: app
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: app
  name: 'ftp'
  properties: {
    allow: false
  }
}

// The mock's own log lines (accepted batches, refused tokens) on the app's file system, for
// `az webapp log tail`. Nothing is sent to Application Insights, so the mock adds nothing to
// the Log Analytics workspace's ingestion.
resource logs 'Microsoft.Web/sites/config@2025-03-01' = {
  parent: app
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Information'
      }
    }
    httpLogs: {
      fileSystem: {
        enabled: true
        retentionInMb: 35
        retentionInDays: 3
      }
    }
  }
}

@description('Name of the web app, for deploying the mock and tailing its log.')
output name string = app.name

@description('Host name of the web app. The Function forwards to https://<this>/v1/readings.')
output defaultHostName string = app.properties.defaultHostName

@description('Name of the archive\'s storage account.')
output archiveStorageAccountName string = archiveStorageAccount.name
