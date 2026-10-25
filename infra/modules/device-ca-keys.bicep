// The signing keys of the device certificate authority (ADR-009, #11): a root CA key and an
// issuing CA key, both generated inside Key Vault and never exported.
//
// Key Vault is not a certificate authority service. These are plain keys: the signing tool in
// tools/IIoT.DeviceCa builds the CA and device certificates itself and asks Key Vault only to
// sign them. The certificates are written by that tool, not by this template.

@description('Name of the Key Vault, in this resource group, that holds the keys.')
param keyVaultName string

@description('Names of the CA keys: the root CA key and the issuing CA key.')
@minLength(1)
param keyNames string[]

@description('Tags applied to the keys.')
param tags { *: string }

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: keyVaultName
}

// One resource per key, identical apart from the name.
//
// Redeploying is safe. The ARM operation behind this resource type, Keys - Create If Not Exist,
// "creates the first version of a new key if it does not exist. If it already exists, then the
// existing key is returned without any write operations being performed. This API does not
// create subsequent versions, and does not update existing keys." (azure-rest-api-specs,
// Microsoft.KeyVault stable 2026-05-15, operation Keys_CreateIfNotExist). So a later deployment
// neither rotates a CA key nor creates a new version of it, and the certificates and devices
// signed with it stay valid. The flip side: changing kty, curveName or keyOps here later has no
// effect on a key that exists. A different key needs a new name, and with it a new CA.
resource caKeys 'Microsoft.KeyVault/vaults/keys@2026-05-15' = [
  for keyName in keyNames: {
    parent: keyVault
    name: keyName
    tags: tags
    properties: {
      // EC P-256 (ECDSA with SHA-256, ES256): the smallest certificates and signatures of the
      // algorithms IoT Hub and DPS accept, which matters because the chain crosses NB-IoT on
      // every connection. 'EC' is a software-protected key, all that the vault's standard SKU
      // offers. Production would use 'EC-HSM' in a premium vault or a Managed HSM, and keep
      // the root offline (ADR-009).
      kty: 'EC'
      curveName: 'P-256'
      // Signing only: a CA key signs certificates and verifies its own signatures, and nothing
      // else may use it.
      keyOps: [
        'sign'
        'verify'
      ]
      attributes: {
        enabled: true
        // The private key never leaves the vault. This is the default; it is stated because it is
        // the property the design rests on.
        exportable: false
      }
    }
  }
]
