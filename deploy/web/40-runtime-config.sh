#!/bin/sh
# Writes /config.json for the Angular client from the environment (Step 14, ADR-0034), so one
# image runs against any identity provider. Served by nginx from /tmp/eka (the web root stays
# read-only to the nginx user).
set -eu

api_base_url="${EKA_API_BASE_URL:-}"
client_id="${EKA_AUTH_CLIENT_ID:-eka-cli}"
scope="${EKA_AUTH_SCOPE:-openid eka.access}"

# The values are written into JSON unescaped, so refuse anything that would need escaping.
for value in "$EKA_AUTH_AUTHORITY" "$api_base_url" "$client_id" "$scope"; do
    case "$value" in
        *\"* | *\\*) echo "runtime config: quotes and backslashes are not allowed in EKA_* values" >&2; exit 1 ;;
    esac
done

mkdir -p /tmp/eka
cat > /tmp/eka/config.json <<EOF
{
  "apiBaseUrl": "${api_base_url}",
  "auth": {
    "authority": "${EKA_AUTH_AUTHORITY}",
    "clientId": "${client_id}",
    "scope": "${scope}"
  }
}
EOF

echo "runtime config: authority=${EKA_AUTH_AUTHORITY} api=${api_base_url:-<same origin>}"
