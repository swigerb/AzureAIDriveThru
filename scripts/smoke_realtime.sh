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

# #302: discover every enabled persona via the SAME PersonaCatalog the app itself uses
# (so this never hardcodes a brand list), then run BOTH the realtime and cascade pipeline
# smoke checks for each one. No persona packs at all (legacy/local dev) falls back to a
# single unlabelled run of each pipeline against the default prompt/tools.
PERSONAS=$("$PYTHON" -c "
import sys
sys.path.insert(0, '$PROJECT_ROOT/app/backend')
try:
    from persona_loader import PersonaCatalog
    print(' '.join(PersonaCatalog.load().ids))
except Exception:
    pass
" 2>/dev/null)
if [ -z "$PERSONAS" ]; then
  PERSONAS="__default__"
fi

for persona in $PERSONAS; do
  for pipeline in realtime cascade; do
    if [ "$persona" = "__default__" ]; then
      label="$pipeline"
      persona_args=""
    else
      label="$persona/$pipeline"
      persona_args="--persona $persona"
    fi
    echo "Realtime smoke check ($label): running..."
    "$PYTHON" "$SCRIPT_DIR/smoke_realtime.py" --pipeline "$pipeline" $persona_args
    report_result "$label" "$?"
  done
done

# S7 (#17 go-live): no second "backend-dotnet" pass here. smoke_realtime.py always builds the
# Python RTMiddleTier against the shared Azure OpenAI realtime deployment -- it never reads
# BACKEND_DOTNET_URI or talks to the .NET app at all, so re-running it a second time would just be
# the same Python-side check twice (burning realtime capacity guests share for zero extra signal).
# The real C# check is `Verify-ProductionAuth.ps1 -Authenticated` against BACKEND_DOTNET_URI in the
# ".NET container app (S7, #17)" rollout in DEPLOY.md. A real .NET realtime smoke probe (a script
# that opens a realtime session against the C# app's own endpoint) is tracked as a follow-up, not
# yet implemented.

exit 0
