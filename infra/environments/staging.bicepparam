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

// The Function's identity provider for the downstream gateway (#12): an Auth0 tenant in the EU
// region. Both values are public: the token endpoint is the tenant's, and the audience is the
// gateway API's identifier, which every token carries in its `aud` claim. `.example` is a
// reserved domain: the identifier names the API and is never called. The client ID and secret
// are in Key Vault (README.md, "Service-to-service authentication (one-time)").
param gatewayAuthTokenEndpoint = 'https://iiot-staging.eu.auth0.com/oauth/token'
param gatewayAuthAudience = 'https://gateway.iiot.example'

// The tenant's issuer, as its tokens carry it in `iss` and its discovery document states it:
// with the trailing slash. The hosted gateway mock (#15) accepts tokens from this issuer only.
param gatewayAuthIssuer = 'https://iiot-staging.eu.auth0.com/'

// Sweden Central, not North Europe: on 2026-10-26 this subscription's App Service quota for the
// free tier (F1) was 0 in North Europe, Germany West Central and UK South, and 1 in Sweden Central
// and France Central (preflight validation, `SubscriptionIsOverQuotaForSku`). The mock is a
// separate component; it stays in the EU, and only its calls from the Function cross regions.
param gatewayMockLocation = 'swedencentral'

// The mock's dashboard (#47): people sign in with the same Auth0 tenant, through an application of
// its own (a Regular Web Application). Its client ID and secret are in Key Vault (README.md,
// "Gateway mock dashboard (one-time)").
param gatewayMockDashboardAuthority = 'https://iiot-staging.eu.auth0.com/'

// The repository whose deploy workflow may deploy the Function's code (#5). It was created after
// 15 July 2026, so GitHub issues it OIDC subjects in the immutable form, with the owner's and the
// repository's numeric IDs after the names: a deleted and re-created, or renamed and re-used,
// repository of the same name cannot match. Both IDs are public. The value is what
// `gh api repos/asnielsen789/IIoT-platform/actions/oidc/customization/sub --jq .sub_claim_prefix`
// reports; main.bicep appends `:environment:staging`.
param githubOidcSubjectPrefix = 'repo:asnielsen789@40421965/IIoT-platform@1371071633'
