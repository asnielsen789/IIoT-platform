// The Device Provisioning Service (DPS), linked to the platform's IoT hub (#48): zero-touch
// provisioning, the second stage of ADR-009's registration decision. A device needs only the
// global DPS endpoint, the ID scope and its certificate; DPS validates the certificate against
// the CA in an enrollment group and creates the device's identity in the hub it assigns.
//
// S1 is DPS's only tier: billed per operation (a registration, a status lookup, an enrollment
// change), with no monthly or unit fee, so an idle instance costs nothing.
//
// The link uses a key, through the hub's own `dps-link` policy, not a managed identity. Linking
// by managed identity is in preview and needs IoT Hub Data Contributor on the hub, the hub's full
// data plane; the policy grants only the two rights DPS documents as necessary, RegistryWrite and
// ServiceConnect. The key is read with listKeys() while the deployment runs and goes straight into
// the link. It is never a parameter or an output, so it appears in no parameter file, deployment
// history or log. Every infrastructure deployment regenerates the hub's keys (ADR-007, update of
// 2026-10-12), and the same deployment writes the new one into the link.

@description('Name of the provisioning service. Globally unique: it is the first label of its service host name.')
param name string

@description('Azure region.')
param location string

@description('Tags applied to the provisioning service.')
param tags { *: string }

@description('Name of the IoT hub DPS provisions devices into.')
param iotHubName string

@description('Shared access policy on the hub that the link uses. Must hold RegistryWrite and ServiceConnect.')
param linkPolicyName string

resource iotHub 'Microsoft.Devices/IotHubs@2023-06-30' existing = {
  name: iotHubName
}

// listKeys() returns every policy on the hub; only the link's own policy is used.
var linkKey = filter(iotHub.listKeys().value, rule => rule.keyName == linkPolicyName)[0].primaryKey

resource provisioningService 'Microsoft.Devices/provisioningServices@2022-02-05' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'S1'
    capacity: 1
  }
  properties: {
    // One linked hub, so the policy decides nothing yet. Hashed is DPS's default: with more
    // hubs, a device keeps landing in the same hub when it provisions again.
    allocationPolicy: 'Hashed'
    iotHubs: [
      {
        // The hub's classic host name, `<hub>.azure-devices.net`, which devices already use.
        // A link created by the portal or the CLI now defaults to the TLS 1.3 device endpoint,
        // `<hub>.device.azure-devices.net`, which is in preview.
        connectionString: 'HostName=${iotHub.properties.hostName};SharedAccessKeyName=${linkPolicyName};SharedAccessKey=${linkKey}'
        location: iotHub.location
        applyAllocationPolicy: true
      }
    ]
  }
}

@description('Name of the provisioning service.')
output name string = provisioningService.name

@description('ID scope a device presents to DPS, with its registration ID. An identifier, not a secret.')
output idScope string = provisioningService.properties.idScope

@description('Host name devices connect to for provisioning: the global endpoint, the same for every instance.')
output deviceProvisioningHostName string = provisioningService.properties.deviceProvisioningHostName
