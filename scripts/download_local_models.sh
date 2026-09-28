#!/usr/bin/env bash
# Thin wrapper -- see scripts/download_local_models.py for the actual logic and full docs.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 "$SCRIPT_DIR/download_local_models.py" "$@"
