// The staging environment: the project's only Azure environment. `dev` is local development
// and has no Azure counterpart.
using '../main.bicep'

param environmentName = 'staging'

// North Europe. West Europe is closed to tenants that have no resources there yet (Azure's
// region access policy, https://aka.ms/locationineligible), and Sweden Central does not
// offer IoT Hub.
param location = 'northeurope'

// Free tier. One per subscription; cannot be upgraded to a paid tier in place.
param iotHubSkuName = 'F1'

// Caps cost. Ten 2048 MB instances use ten cores, inside the 15-core regional Flex
// Consumption quota of a free trial subscription.
param functionAppMaximumInstanceCount = 10

// Caps cost. 50 MB a day is about fifteen times staging's busiest day so far (3.2 MB, during the
// first deployment's crash loop), so only a surge reaches it. At most about 1.5 GB a month: a
// small part of the 100 DKK monthly budget (#1) even if all of it were billed.
param logAnalyticsDailyQuotaGb = '0.05'

// The repository whose deploy workflow may deploy the Function's code (#5). It was created after
// 15 July 2026, so GitHub issues it OIDC subjects in the immutable form, with the owner's and the
// repository's numeric IDs after the names: a deleted and re-created, or renamed and re-used,
// repository of the same name cannot match. Both IDs are public. The value is what
// `gh api repos/asnielsen789/IIoT-platform/actions/oidc/customization/sub --jq .sub_claim_prefix`
// reports; main.bicep appends `:environment:staging`.
param githubOidcSubjectPrefix = 'repo:asnielsen789@40421965/IIoT-platform@1371071633'
