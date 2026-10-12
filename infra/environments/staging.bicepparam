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
