#!/bin/sh
# azd postdeploy hook: run the realtime session smoke check (scripts/smoke_realtime.py).
# It NEVER fails the deployment: on any problem it prints a loud warning and exits 0.
# Skip it with:  azd env set SONIC_SKIP_REALTIME_SMOKE true

if [ "$SONIC_SKIP_REALTIME_SMOKE" = "true" ]; then
  echo "Realtime smoke check skipped (SONIC_SKIP_REALTIME_SMOKE=true)."
  exit 0
fi

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
PROJECT_ROOT=$(dirname "$SCRIPT_DIR")
PYTHON=""
for candidate in "$PROJECT_ROOT/app/backend/.venv/bin/python" "$PROJECT_ROOT/.venv/bin/python"; do
  if [ -x "$candidate" ]; then
    PYTHON="$candidate"
    break
  fi
done
if [ -z "$PYTHON" ]; then
  echo "WARNING: Realtime smoke check skipped: no Python virtual environment found (run the postprovision hook first)."
  exit 0
fi

# Prints a loud (non-fatal) warning for one labeled run's exit code; never changes the hook's own
# exit status -- the whole hook stays non-fatal to the deployment, per the module doc above.
report_result() {
  label="$1"
  code="$2"
  if [ "$code" -ne 0 ]; then
    echo ""
    echo "WARNING: =================================================================="
    if [ "$code" -eq 1 ]; then
      echo "WARNING:  REALTIME SMOKE CHECK FAILED ($label): the live deployment rejected part"
      echo "WARNING:  of the carhop's session config (tools may NOT register) or did not"
      echo "WARNING:  transcribe the test phrase word for word. See above."
    else
      echo "WARNING:  Realtime smoke check ($label) could not run (exit $code) - auth, network"
      echo "WARNING:  or missing settings. Right after a first provision the OpenAI role"
      echo "WARNING:  assignment can take a few minutes to apply; rerun with:"
      echo "WARNING:    python scripts/smoke_realtime.py"
    fi
    echo "WARNING:  The deployment itself was NOT rolled back."
    echo "WARNING: =================================================================="
  fi
}

echo "Realtime smoke check (backend): running..."
"$PYTHON" "$SCRIPT_DIR/smoke_realtime.py"
report_result "backend" "$?"

# S7 (#17 go-live): once BACKEND_DOTNET_URI is populated (deployDotnetApp=true, infra/main.bicep),
# re-run the SAME check a second time so a postdeploy against the .NET app's rollout also gets a
# fresh realtime-session verification, not just whatever the Python app's last run happened to
# confirm. This talks directly to the shared Azure OpenAI realtime deployment (not either app's own
# HTTP endpoint -- see the module doc in smoke_realtime.py), which is why the two runs MUST be
# serial, never concurrent: the realtime deployment's capacity may be provisioned as low as 10
# concurrent sessions (AZURE_OPENAI_REALTIME_DEPLOYMENT_CAPACITY), and this hook already competes
# with real guest traffic for that same quota.
dotnet_uri="$(azd env get-value BACKEND_DOTNET_URI 2>/dev/null || true)"
if [ -n "$dotnet_uri" ]; then
  echo ""
  echo "Realtime smoke check (backend-dotnet): BACKEND_DOTNET_URI is set -- running a second, serial pass..."
  "$PYTHON" "$SCRIPT_DIR/smoke_realtime.py"
  report_result "backend-dotnet" "$?"
else
  echo "Realtime smoke check (backend-dotnet): BACKEND_DOTNET_URI is empty -- skipping (the .NET app is not deployed, #17)."
fi

exit 0
