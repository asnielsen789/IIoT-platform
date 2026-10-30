#!/usr/bin/env bash
# Provisions a simulated fleet through DPS (#79, step 6): for each device of the fleet file,
#
#   IIoT.TestClient create-csr       a key and a CSR, unless the device has a key already
#   IIoT.DeviceCa issue --no-register  a certificate from the device CA, unless it has one already
#   IIoT.TestClient provision --no-send  registration through DPS, which creates the hub identity
#
# the same three steps as for one device (tools/IIoT.TestClient/README.md, "Zero-touch
# provisioning"). Keys and certificates stay in the secrets directory, outside the repository.
#
# A device that the hub already knows by other means is left alone: a shared access key, or a
# CA-signed identity that `IIoT.DeviceCa issue --hub` registered. Provisioning it through DPS would
# replace that credential with the certificate's thumbprint. A device DPS provisioned before is
# provisioned again, so the hub holds the certificate that is in the secrets directory.
#
# Run from the repository root, in Git Bash, signed in to the Azure CLI as the operator: issuing
# needs Key Vault Crypto User on the issuing CA key and Secrets User on the CA certificates
# (infra/README.md, "Device certificate authority (one-time)"); the check of the hub's registry
# needs IoT Hub Registry Contributor or Reader.
#
#   tools/provision-fleet.sh --vault <name> --hub <name> --id-scope <scope> \
#     [--fleet <file>] [--secrets <dir>] [--device <id>]...
set -euo pipefail

usage() {
  sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

FLEET="tools/IIoT.TestClient/Fleet/staging-fleet.json"
SECRETS="$HOME/.secrets/iiot"
VAULT=""
HUB=""
ID_SCOPE=""
SELECT=()

while [ $# -gt 0 ]; do
  case "$1" in
    --vault) VAULT="$2"; shift 2 ;;
    --hub) HUB="$2"; shift 2 ;;
    --id-scope) ID_SCOPE="$2"; shift 2 ;;
    --fleet) FLEET="$2"; shift 2 ;;
    --secrets) SECRETS="$2"; shift 2 ;;
    --device) SELECT+=(--device "$2"); shift 2 ;;
    -h|--help) usage ;;
    *) echo "Unknown option '$1'." >&2; usage ;;
  esac
done

if [ -z "$VAULT" ] || [ -z "$HUB" ] || [ -z "$ID_SCOPE" ]; then
  echo "--vault, --hub and --id-scope are required." >&2
  usage
fi

dotnet build tools/IIoT.TestClient tools/IIoT.DeviceCa -c Release -v q --nologo >/dev/null
CLIENT=(dotnet tools/IIoT.TestClient/bin/Release/net10.0/IIoT.TestClient.dll)
CA=(dotnet tools/IIoT.DeviceCa/bin/Release/net10.0/IIoT.DeviceCa.dll)

mapfile -t DEVICES < <("${CLIENT[@]}" fleet --list --fleet "$FLEET" "${SELECT[@]}" | tr -d '\r')

provisioned=0
left=0
failed=0
for id in "${DEVICES[@]}"; do
  # Empty when the hub does not know the device.
  auth=$(az iot hub device-identity show --hub-name "$HUB" --device-id "$id" \
    --query authentication.type -o tsv 2>/dev/null | tr -d '\r' || true)

  case "$auth" in
    sas|certificateAuthority)
      echo "[$id] registered in the hub with '$auth': left alone"
      left=$((left + 1))
      continue
      ;;
  esac

  if ! {
    [ -f "$SECRETS/$id.key.pem" ] || "${CLIENT[@]}" create-csr --device-id "$id" --out "$SECRETS" >/dev/null
    [ -f "$SECRETS/$id.chain.pem" ] || "${CA[@]}" issue --vault "$VAULT" --no-register \
      --csr "$SECRETS/$id.csr.pem" --device-id "$id" --out "$SECRETS" >/dev/null
    "${CLIENT[@]}" provision --id-scope "$ID_SCOPE" \
      --cert "$SECRETS/$id.chain.pem" --key "$SECRETS/$id.key.pem" --no-send | grep -E "assigned|refused"
  }; then
    echo "[$id] failed"
    failed=$((failed + 1))
    continue
  fi

  provisioned=$((provisioned + 1))
done

echo "Provisioned $provisioned, left alone $left, failed $failed."
[ "$failed" -eq 0 ]
