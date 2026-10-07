// The staging environment: the project's only Azure environment. `dev` is local development
// and has no Azure counterpart.
using '../main.bicep'

param environmentName = 'staging'

// West Europe: IoT Hub is not offered in Sweden Central.
param location = 'westeurope'

// Free tier. One per subscription; cannot be upgraded to a paid tier in place.
param iotHubSkuName = 'F1'

// Caps cost. Ten 2048 MB instances use ten cores, inside the 15-core regional Flex
// Consumption quota of a free trial subscription.
param functionAppMaximumInstanceCount = 10
