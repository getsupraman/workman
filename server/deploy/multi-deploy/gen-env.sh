#!/usr/bin/env bash
# Writes this server's .env once: its own random tokens and passwords, generated here and never
# copied from another machine. Refuses to overwrite an existing .env. Prints no secret.
#   ./gen-env.sh <host> [version]      e.g. ./gen-env.sh workman.example.com 2.0.0
set -euo pipefail

cd "$(dirname "$0")"
host=${1:?usage: gen-env.sh <host> [version]}
version=${2:-2.0.0}

if [[ -e .env ]]; then echo ".env exists; leaving it alone" >&2; exit 1; fi

subnet=$(docker network inspect web -f '{{range .IPAM.Config}}{{.Subnet}} {{end}}' | awk '{print $1}')
if [[ -z "$subnet" ]]; then echo "no subnet on docker network 'web'; is multi-deploy's Traefik up?" >&2; exit 1; fi

secret() { openssl rand -hex 32; }

umask 077
cat > .env <<EOF
WORKMAN_VERSION=$version
WORKMAN_HOST=$host
WORKMAN_PUBLIC_URL=https://$host
WORKMAN_TRUSTED_PROXIES=$subnet

WORKMAN_DASHBOARD_USERNAME=operator
WORKMAN_DASHBOARD_PASSWORD=$(secret)

POSTGRES_DB=workman
POSTGRES_USER=workman
POSTGRES_PASSWORD=$(secret)
EOF

echo "wrote .env (mode 600): version $version, https://$host, trusted proxies $subnet"
echo "read the dashboard password on this server with: grep ^WORKMAN_DASHBOARD_PASSWORD .env"
