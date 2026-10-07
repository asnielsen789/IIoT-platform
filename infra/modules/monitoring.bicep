// Log Analytics workspace and workspace-based Application Insights.
//
// The Function's worker exports through APPLICATIONINSIGHTS_CONNECTION_STRING (see
// src/IIoT.Functions/Program.cs). Instrumentation-key ingestion stays enabled: the worker does
// not authenticate to Application Insights with Entra ID, so disabling local authentication
// would make Application Insights reject its telemetry, including the security events it
// logs. Entra-only ingestion is a hardening option for the security review (#21).

@description('Name of the Log Analytics workspace.')
param logAnalyticsName string

@description('Name of the Application Insights resource.')
param applicationInsightsName string

@description('Azure region.')
param location string

@description('Tags applied to both resources.')
param tags { *: string }

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2026-03-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    // The minimum for PerGB2018, and covered by the ingestion price.
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

@description('Name of the Application Insights resource. The connection string is read where it is used, not passed around as an output.')
output applicationInsightsName string = applicationInsights.name

@description('Resource ID of the Log Analytics workspace.')
output logAnalyticsId string = logAnalytics.id
