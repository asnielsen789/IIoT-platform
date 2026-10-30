// The downstream gateway mock (tools/IIoT.GatewayMock), hosted so that the Function in Azure has
// a gateway to forward to (#15). A development tool, not a gateway: it validates the readings
// contract and the bearer token, and keeps what it accepts in memory.
//
// App Service on the free tier (F1), Linux: no cost, HTTPS on its azurewebsites.net name with a
// platform certificate, and the .NET 10 runtime built in, so the mock is deployed as a plain
// `dotnet publish` output (infra/README.md, "Gateway mock"). F1 has no Always On: the app
// unloads when idle, and the first request after that waits for it to start. That suits a mock
// that receives a message every few hours. It also has a daily CPU quota of 60 minutes.
//
// The API needs no Azure access. Its configuration is three public values, the issuer and
// audience it accepts tokens for. The dashboard (#47) is optional: with `dashboardAuthority` set,
// the app gets a system-assigned identity that can read exactly two Key Vault secrets, the
// dashboard's client ID and secret at the identity provider, through Key Vault references.
// Without it, the app has no identity and no role, as before.

@description('Name of the App Service plan.')
param planName string

@description('Name of the web app. Globally unique: it is the first label of its azurewebsites.net host name.')
param name string

@description('Azure region.')
param location string

@description('Tags applied to both resources.')
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

var dashboard = !empty(dashboardAuthority)

// Built-in role definition ID.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

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
  // System-assigned: it exists only for this app's Key Vault references, and goes with the app.
  identity: {
    type: dashboard ? 'SystemAssigned' : 'None'
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
