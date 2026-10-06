# azd postdeploy hook: run the realtime session smoke check (scripts/smoke_realtime.py).
# It NEVER fails the deployment: on any problem it prints a loud warning and exits 0.
# Skip it with:  azd env set SONIC_SKIP_REALTIME_SMOKE true
$ErrorActionPreference = "Continue"

if ($env:SONIC_SKIP_REALTIME_SMOKE -eq "true") {
  Write-Host "Realtime smoke check skipped (SONIC_SKIP_REALTIME_SMOKE=true)."
  exit 0
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$candidates = @(
  (Join-Path $projectRoot ".venv\Scripts\python.exe"),
  (Join-Path $projectRoot ".venv/bin/python"),
  (Join-Path $projectRoot "app/backend/.venv/bin/python")
)
$python = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $python) {
  Write-Warning "Realtime smoke check skipped: no Python virtual environment found (run the postprovision hook first)."
  exit 0
}

# Prints a loud (non-fatal) warning for one labeled run's exit code; never changes the hook's own
# exit status -- the whole hook stays non-fatal to the deployment, per the module doc above.
function Report-Result([string]$Label, [int]$Code) {
  if ($Code -ne 0) {
    Write-Host ""
    Write-Warning "=================================================================="
    if ($Code -eq 1) {
      Write-Warning " REALTIME SMOKE CHECK FAILED ($Label): the live deployment rejected part"
      Write-Warning " of the carhop's session config (tools may NOT register) or did not"
      Write-Warning " transcribe the test phrase word for word. See above."
    } else {
      Write-Warning " Realtime smoke check ($Label) could not run (exit $Code) - auth, network"
      Write-Warning " or missing settings. Right after a first provision the OpenAI role"
      Write-Warning " assignment can take a few minutes to apply; rerun with:"
      Write-Warning "   python scripts/smoke_realtime.py"
    }
    Write-Warning " The deployment itself was NOT rolled back."
    Write-Warning "=================================================================="
  }
}

# #302: discover every enabled persona via the SAME PersonaCatalog the app itself uses
# (so this never hardcodes a brand list), then run BOTH the realtime and cascade pipeline
# smoke checks for each one. No persona packs at all (legacy/local dev) falls back to a
# single unlabelled run of each pipeline against the default prompt/tools.
$personaScript = @"
import sys
sys.path.insert(0, r'$projectRoot/app/backend')
try:
    from persona_loader import PersonaCatalog
    print(' '.join(PersonaCatalog.load().ids))
except Exception:
    pass
"@
$personasOutput = (& $python -c $personaScript 2>$null)
$personas = @()
if ($personasOutput) {
  $personas = ($personasOutput -split '\s+') | Where-Object { $_ -ne "" }
}
if ($personas.Count -eq 0) {
  $personas = @("__default__")
}

foreach ($persona in $personas) {
  foreach ($pipeline in @("realtime", "cascade")) {
    if ($persona -eq "__default__") {
      $label = $pipeline
      $personaArgs = @()
    } else {
      $label = "$persona/$pipeline"
      $personaArgs = @("--persona", $persona)
    }
    Write-Host "Realtime smoke check ($label): running..."
    & $python (Join-Path $PSScriptRoot "smoke_realtime.py") --pipeline $pipeline @personaArgs
    Report-Result -Label $label -Code $LASTEXITCODE
  }
}

# S7 (#17 go-live): no second "backend-dotnet" pass here. smoke_realtime.py always builds the
# Python RTMiddleTier against the shared Azure OpenAI realtime deployment -- it never reads
# BACKEND_DOTNET_URI or talks to the .NET app at all, so re-running it a second time would just be
# the same Python-side check twice (burning realtime capacity guests share for zero extra signal).
# The real C# check is `Verify-ProductionAuth.ps1 -Authenticated` against BACKEND_DOTNET_URI in the
# ".NET container app (S7, #17)" rollout in DEPLOY.md. A real .NET realtime smoke probe (a script
# that opens a realtime session against the C# app's own endpoint) is tracked as a follow-up, not
# yet implemented.

exit 0
