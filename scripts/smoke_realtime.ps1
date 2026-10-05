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

Write-Host "Realtime smoke check (backend): running..."
& $python (Join-Path $PSScriptRoot "smoke_realtime.py")
Report-Result -Label "backend" -Code $LASTEXITCODE

# S7 (#17 go-live): once BACKEND_DOTNET_URI is populated (deployDotnetApp=true, infra/main.bicep),
# re-run the SAME check a second time so a postdeploy against the .NET app's rollout also gets a
# fresh realtime-session verification, not just whatever the Python app's last run happened to
# confirm. This talks directly to the shared Azure OpenAI realtime deployment (not either app's own
# HTTP endpoint -- see the module doc in smoke_realtime.py), which is why the two runs MUST be
# serial, never concurrent: the realtime deployment's capacity may be provisioned as low as 10
# concurrent sessions (AZURE_OPENAI_REALTIME_DEPLOYMENT_CAPACITY), and this hook already competes
# with real guest traffic for that same quota.
$dotnetUri = (azd env get-value BACKEND_DOTNET_URI 2>$null | Out-String).Trim()
if ($dotnetUri) {
  Write-Host ""
  Write-Host "Realtime smoke check (backend-dotnet): BACKEND_DOTNET_URI is set -- running a second, serial pass..."
  & $python (Join-Path $PSScriptRoot "smoke_realtime.py")
  Report-Result -Label "backend-dotnet" -Code $LASTEXITCODE
} else {
  Write-Host "Realtime smoke check (backend-dotnet): BACKEND_DOTNET_URI is empty -- skipping (the .NET app is not deployed, #17)."
}

exit 0
