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
// The mock needs no Azure access: no managed identity, no Key Vault, no role assignments. Its
// configuration is three public values, the issuer and audience it accepts tokens for.

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
      appSettings: [
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
      ]
    }
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
