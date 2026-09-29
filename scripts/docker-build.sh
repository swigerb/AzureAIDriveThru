#!/bin/bash
set -euo pipefail

# ADR-002 / design 18.8: frontend auth config travels as Docker build args, not a generated
# app/frontend/.env (removed -- see .dockerignore and app/Dockerfile). Reads the same azd env
# names Setup-EntraAuth.ps1 prints and azure.yaml's docker.buildArgs use, so a local build matches
# what azd would build. Falling back to Development when the ids are unset keeps this script usable
# without a full Entra setup (e.g. a quick local smoke test).
VITE_AUTH_MODE="${VITE_AUTH_MODE:-Entra}"
VITE_ENTRA_TENANT_ID="${VITE_ENTRA_TENANT_ID:-$(azd env get-value ENTRA_TENANT_ID 2>/dev/null || true)}"
VITE_ENTRA_CLIENT_ID="${VITE_ENTRA_CLIENT_ID:-$(azd env get-value ENTRA_CLIENT_ID 2>/dev/null || true)}"
VITE_ENTRA_API_SCOPE="${VITE_ENTRA_API_SCOPE:-$(azd env get-value ENTRA_API_SCOPE 2>/dev/null || echo access_as_user)}"

if [ "$VITE_AUTH_MODE" = "Entra" ] && { [ -z "$VITE_ENTRA_TENANT_ID" ] || [ -z "$VITE_ENTRA_CLIENT_ID" ]; }; then
  echo "⚠️  VITE_AUTH_MODE=Entra but ENTRA_TENANT_ID/ENTRA_CLIENT_ID are not set (run Setup-EntraAuth.ps1" >&2
  echo "   and 'azd env set ENTRA_TENANT_ID ...'/'azd env set ENTRA_CLIENT_ID ...' first, or pass" >&2
  echo "   VITE_AUTH_MODE=Development for a smoke-test build)." >&2
  echo "   Falling back to VITE_AUTH_MODE=Development for this build." >&2
  VITE_AUTH_MODE="Development"
  VITE_ENTRA_TENANT_ID=""
  VITE_ENTRA_CLIENT_ID=""
fi

# Build the Docker image (frontend auth config is read from the build args above).
echo "🔨 Building Docker image..."
# Issue #129: the build context is the repo root, the same as CI and azd (azure.yaml docker.context).
docker build --no-cache -t sonic-drive-thru-app -f ./app/Dockerfile \
  --build-arg VITE_AUTH_MODE="$VITE_AUTH_MODE" \
  --build-arg VITE_ENTRA_TENANT_ID="$VITE_ENTRA_TENANT_ID" \
  --build-arg VITE_ENTRA_CLIENT_ID="$VITE_ENTRA_CLIENT_ID" \
  --build-arg VITE_ENTRA_API_SCOPE="$VITE_ENTRA_API_SCOPE" \
  .

# Run the container
echo "🚀 Running Docker container..."
docker run -p 8000:8000 --env-file ./app/backend/.env sonic-drive-thru-app:latest
