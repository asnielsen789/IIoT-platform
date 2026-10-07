// IoT Hub: device registry and MQTT/TLS ingestion point.
//
// Message routing is deliberately absent: it belongs to #9. No routes, no custom endpoints and
// no fallback route are declared here. Without a fallback route, telemetry may not reach the
// built-in endpoint the Function reads; see "Open points" in infra/README.md.

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

    // Declaring authorizationPolicies replaces the hub's default policy list, so the five
    // defaults are restated here alongside the dedicated policy; the `az iot hub` tooling relies
    // on them. Trimming the defaults is a separate hardening decision for the security review
    // (#21).
    //
    // RegistryWrite implies RegistryRead, so 'RegistryWrite, ServiceConnect, DeviceConnect' is
    // all permissions: the form IoT Hub reports for its defaults, and the one Microsoft's own
    // quickstart templates use.
    //
    // No keys are given: Azure generates every key. Whether a redeployment keeps existing keys
    // is unverified; see "Open points" in infra/README.md.
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
    ]

    // disableLocalAuth is deliberately not set (it defaults to false): the Function's connection
    // depends on hub-level SAS (ADR-007). disableDeviceSAS is not set either: the MQTT test
    // client authenticates with device symmetric keys.
  }
}

@description('Name of the IoT Hub.')
output name string = iotHub.name

@description('Resource ID of the IoT Hub.')
output id string = iotHub.id

@description('Host name devices connect to.')
output hostName string = iotHub.properties.hostName
