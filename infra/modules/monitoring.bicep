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

@description('Daily ingestion cap of the workspace, in GB, as a decimal string (Bicep has no floating-point type). \'-1\' removes the cap.')
param dailyQuotaGb string

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
    // A backstop against a surge, not a filter. Once a day's billable ingestion reaches the cap,
    // collection stops until the workspace's daily reset, at an hour Azure picks; some excess is
    // still ingested and billed. Application Insights writes into this workspace, and for a
    // workspace-based component the effective cap is the lower of this one and its own, so this
    // one governs. https://learn.microsoft.com/azure/azure-monitor/logs/daily-cap
    workspaceCapping: {
      dailyQuotaGb: json(dailyQuotaGb)
    }
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
