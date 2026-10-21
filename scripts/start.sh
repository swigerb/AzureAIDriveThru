#!/bin/sh
set -e

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PROJECT_ROOT=$(dirname -- "$SCRIPT_DIR")
cd "$PROJECT_ROOT"

PRODUCTION_MODE="${1:-}"

echo 'Creating Python virtual environment and installing dependencies...'
sh scripts/load_python_env.sh

if [ "$PRODUCTION_MODE" != "--production" ]; then
    echo ""
    echo "Restoring frontend npm packages"
    echo ""
    cd app/frontend
    npm install

    echo ""
    echo "Building frontend"
    echo ""
    # ADR-002 / design 18.6, PR GH-148 review round 2 item R4: `vite.config.ts`'s auth-mode build
    # guard now fails closed on an unset VITE_AUTH_MODE with no Entra ids configured (this is a
    # local dev build, so there are none) -- every local developer's `npm run build` here would
    # otherwise fail. Local dev is the intentional unauthenticated pass-through case, so pin
    # VITE_AUTH_MODE=Development unless the caller already set one (an Entra dev box testing real
    # sign-in, say).
    export VITE_AUTH_MODE="${VITE_AUTH_MODE:-Development}"
    npm run build
    cd ../../
fi

echo ""
echo "Starting backend"
echo ""

if [ "$PRODUCTION_MODE" = "--production" ]; then
    HOST="${HOST:-0.0.0.0}"
    PORT="${PORT:-8000}"
    LOG_LEVEL="${LOG_LEVEL:-info}"
    export RUNNING_IN_PRODUCTION=true
    cd app/backend
    "$PROJECT_ROOT/.venv/bin/python" -m gunicorn app:create_app \
        -b "${HOST}:${PORT}" \
        --worker-class aiohttp.GunicornWebWorker \
        --workers 1 \
        --timeout 120 \
        --keep-alive 65 \
        --access-logfile - \
        --log-level "${LOG_LEVEL}"
else
    cd app/backend
    "$PROJECT_ROOT/.venv/bin/python" -m app
fi