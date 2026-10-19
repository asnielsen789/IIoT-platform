// The identity GitHub Actions deploys the Function's code with (#5): a user-assigned managed
// identity, a federated identity credential that trusts GitHub's OIDC tokens for one repository
// and one GitHub environment, and a single role assignment, on the Function App alone.
//
// No secret exists for it anywhere. A workflow job asks GitHub for a short-lived OIDC token, and
// Microsoft Entra ID exchanges that token for an access token for this identity only if its
// issuer, subject and audience match the credential below. Nothing is stored in GitHub that
// could leak or would need rotating; the client, tenant and subscription IDs the workflow passes
// are identifiers, not credentials. A managed identity rather than an app registration because
// it is an Azure resource like any other: the template creates it, scopes it and deletes it with
// the resource group, and no one needs rights in Entra ID to manage it. Microsoft's guide for
// GitHub Actions with Azure Functions uses the same pattern.
//
// Deliberately separate from the identity the app runs as (identity.bicep). That one reads host
// storage and the Key Vault secret and has no right on the app itself; this one may change the
// app's code and configuration and has no right on storage, Key Vault or the IoT Hub. Neither
// holds the other's rights: the running app cannot redeploy itself, and the deploying workflow
// cannot reach the vault, the storage account or the hub directly. Deploying code is still the
// power to change what the app does, so this identity is as sensitive as the app itself.

@description('Name of the user-assigned managed identity.')
param name string

@description('Azure region.')
param location string

@description('Tags applied to the identity.')
param tags { *: string }

@description('Subject the federated credential trusts: the `sub` claim of the GitHub OIDC token, for example `repo:OWNER@OWNER-ID/REPO@REPO-ID:environment:staging`. Matched exactly, case included.')
param githubSubject string

@description('Name of the federated identity credential.')
param credentialName string

@description('Name of the Function App, in this resource group, that the identity deploys to.')
param functionAppName string

// Built-in role definition ID.
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: name
  location: location
  tags: tags
}

// Trusts tokens from GitHub's issuer, for the one subject, with the audience Microsoft documents
// for workload identity federation (and the default of the azure/login action).
//
// The subject binds the trust to a repository and a GitHub environment, not to a branch: any job
// in the repository that declares `environment: staging` receives this subject. What limits that
// to `main` is the environment's deployment branch policy, set up in GitHub (infra/README.md,
// "Continuous deployment"). Without that policy, a workflow on any branch could deploy.
resource githubCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: identity
  name: credentialName
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource functionApp 'Microsoft.Web/sites@2025-03-01' existing = {
  name: functionAppName
}

// Website Contributor on the Function App only: Microsoft's documented role for deploying with
// Azure/functions-action. Every call the deploy workflow makes that reads or changes something
// is on the app resource or its SCM site: the action reads the app, its app settings and
// publishing credentials and posts the package to One Deploy; the workflow refreshes the Key
// Vault references, syncs the triggers and lists the functions. The login's subscription list
// and the action's search for the app by name are answered with only what the caller may read,
// so they need no role of their own. No storage role: on Flex Consumption the deployment
// service writes the package to the deployment container with the app's own identity
// (function-app.bicep), never the deployer's. No right on the plan, the storage account, Key
// Vault or IoT Hub. The role does allow changing app settings, which a code deployment never
// needs; there is no narrower built-in role, and a custom one is an item for the security
// review (#21).
//
// The name is seeded with the identity's resource ID, which is known before deployment, so
// what-if can evaluate it (as in key-vault.bicep).
resource websiteContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionApp.id, identity.id, websiteContributorRoleId)
  scope: functionApp
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Client ID, which the deploy workflow logs in with (the GitHub environment variable AZURE_CLIENT_ID).')
output clientId string = identity.properties.clientId
