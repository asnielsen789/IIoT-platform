// IoT Hub: device registry and MQTT/TLS ingestion point, with the message routing that delivers
// device telemetry to the built-in endpoint the Function reads (#9, ADR-007), the Function's own
// consumer group on that endpoint, and resource logs for routing and device connections.

@description('IoT Hub name: 3-50 characters, globally unique (<name>.azure-devices.net).')
@minLength(3)
@maxLength(50)
param name string

@description('Azure region.')
param location string

@description('Tags applied to the hub.')
param tags { *: string }

@description('IoT Hub SKU. F1 is free, limited to one per subscription, and cannot be upgraded to B or S in place.')
@allowed([
  'F1'
  'S1'
  'S2'
  'S3'
])
param skuName string

@description('Name of the dedicated shared access policy the Function reads telemetry with.')
param telemetryReaderPolicyName string

@description('Name of the dedicated shared access policy the Device Provisioning Service links to the hub with.')
param dpsLinkPolicyName string

@description('Consumer group on the built-in endpoint that the Function reads, and no other reader uses.')
param telemetryConsumerGroupName string

@description('Name of the Log Analytics workspace, in this resource group, that receives the hub\'s resource logs.')
param logAnalyticsWorkspaceName string

// The Event Hubs-compatible built-in endpoint. Its name is fixed by IoT Hub.
var builtInEndpointName = 'events'

resource iotHub 'Microsoft.Devices/IotHubs@2023-06-30' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    // F1 allows exactly one unit; one is also enough for any paid tier at this scale.
    capacity: 1
  }
  properties: {
    // Reject TLS below 1.2 and weak cipher suites. Supported in all public cloud regions.
    minTlsVersion: '1.2'

    // The built-in, Event Hubs-compatible endpoint (messages/events) the Function reads.
    eventHubEndpoints: {
      events: {
        // Fixed at creation. F1 hubs are created with two partitions in Microsoft's own F1
        // examples; four is Microsoft's default and enough for most hubs on paid tiers.
        partitionCount: skuName == 'F1' ? 2 : 4
        // One day, the default, which every tier accepts. Messages the Function has not read
        // within a day expire.
        retentionTimeInDays: 1
      }
    }

    // Message routing (#9). Declared in full rather than left to defaults: once any route exists,
    // the built-in endpoint receives only what a route or the fallback route sends it, and a
    // template without a fallback route leaves it disabled.
    //
    // No custom endpoints: everything goes to the built-in endpoint (ADR-007), so F1's one extra
    // endpoint stays unused. One route is well within F1's five routing queries.
    routing: {
      routes: [
        // Telemetry that declares contract version 1. Querying the body only works when the
        // device sends contentType application/json and contentEncoding utf-8, exactly as
        // section 1 of docs/mqtt-payload.md requires; without them IoT Hub does not evaluate the
        // query. A message delivered by this route is therefore live proof that the device's
        // property bag is right, and the hub's "Routing: messages delivered to fallback" metric
        // counts every message the route did not match.
        {
          name: 'telemetry-v1'
          source: 'DeviceMessages'
          condition: '$body.schemaVersion = 1'
          endpointNames: [
            builtInEndpointName
          ]
          isEnabled: true
        }
      ]
      // Everything the route does not match (another schema version, a body that is not JSON,
      // or a device that left out the content properties) still reaches the Function, which
      // rejects it with a logged reason. With the fallback route disabled, IoT Hub would drop
      // such messages without a trace. When the route could not be evaluated at all, the reason
      // is logged in the hub's Routes category (below).
      fallbackRoute: {
        name: '$fallback'
        source: 'DeviceMessages'
        condition: 'true'
        endpointNames: [
          builtInEndpointName
        ]
        isEnabled: true
      }
    }

    // Declaring authorizationPolicies replaces the hub's default policy list, so the five
    // defaults are restated here alongside the dedicated policy; the `az iot hub` tooling relies
    // on them. Trimming the defaults is a separate hardening decision for the security review
    // (#21).
    //
    // RegistryWrite implies RegistryRead, so 'RegistryWrite, ServiceConnect, DeviceConnect' is
    // all permissions: the form IoT Hub reports for its defaults, and the one Microsoft's own
    // quickstart templates use.
    //
    // No keys are given: Azure generates every key, and every deployment regenerates them all
    // (ADR-007, update of 2026-10-12). See "Deploy" in infra/README.md for the refresh this
    // requires.
    authorizationPolicies: [
      {
        keyName: 'iothubowner'
        rights: 'RegistryWrite, ServiceConnect, DeviceConnect'
      }
      {
        keyName: 'service'
        rights: 'ServiceConnect'
      }
      {
        keyName: 'device'
        rights: 'DeviceConnect'
      }
      {
        keyName: 'registryRead'
        rights: 'RegistryRead'
      }
      {
        keyName: 'registryReadWrite'
        rights: 'RegistryWrite'
      }
      // The Function's own policy (ADR-007): ServiceConnect only, so its key can be rotated or
      // revoked without touching anything else that talks to the hub.
      {
        keyName: telemetryReaderPolicyName
        rights: 'ServiceConnect'
      }
      // The Device Provisioning Service's link (#48): the two rights DPS documents as necessary
      // to create device identities, and nothing else. Its own policy, for the same reason as
      // the Function's.
      {
        keyName: dpsLinkPolicyName
        rights: 'RegistryWrite, ServiceConnect'
      }
    ]

    // disableLocalAuth is deliberately not set (it defaults to false): the Function's connection
    // depends on hub-level SAS (ADR-007). disableDeviceSAS is not set either: the MQTT test
    // client authenticates with device symmetric keys.
  }
}

// The Function's own consumer group on the built-in endpoint. Readers in one consumer group
// compete for its partitions, and `az iot hub monitor-events` reads `$Default` unless told
// otherwise, so the Function stays out of `$Default`. A hub allows 20 consumer groups on the
// built-in endpoint, F1 included. The Function App's IoTHubConsumerGroup setting carries this
// name to the trigger.
resource telemetryConsumerGroup 'Microsoft.Devices/IotHubs/eventHubEndpoints/ConsumerGroups@2023-06-30' = {
  name: '${iotHub.name}/${builtInEndpointName}/${telemetryConsumerGroupName}'
  properties: {
    name: telemetryConsumerGroupName
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2026-03-01' existing = {
  name: logAnalyticsWorkspaceName
}

// Resource logs to Log Analytics, two categories only. Both land in the AzureDiagnostics table.
// - Routes: route evaluation problems and endpoint health. A message without valid content
//   properties is logged as RouteEvaluationError, a query on a missing field as
//   UndefinedRouteEvaluation (both at most once a minute per hub). This category is the only
//   place a routing failure is recorded, so it is what makes a silent routing failure visible.
//   Successful deliveries are not logged: a healthy hub writes nothing here.
// - Connections: device connects and disconnects, and failed connection attempts.
// Platform metrics, including the routing metrics, are available without a diagnostic setting
// and are not exported.
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'routes-and-connections'
  scope: iotHub
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'Routes'
        enabled: true
      }
      {
        category: 'Connections'
        enabled: true
      }
    ]
  }
}

@description('Name of the IoT Hub.')
output name string = iotHub.name

@description('Resource ID of the IoT Hub.')
output id string = iotHub.id

@description('Host name devices connect to.')
output hostName string = iotHub.properties.hostName
