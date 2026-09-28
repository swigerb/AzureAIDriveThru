# Production mode: pass -Production flag to skip frontend rebuild and use gunicorn
param(
    [switch]$Production
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")

Push-Location $repoRoot
try {
    & "$repoRoot/scripts/load_python_env.ps1"

    if (-not $Production) {
        Write-Host ""
        Write-Host "Restoring frontend npm packages"
        Write-Host ""
        Push-Location "$repoRoot/app/frontend"
        # ADR-002 / design 18.6, PR GH-148 review round 2 item R4: `vite.config.ts`'s auth-mode
        # build guard now fails closed on an unset VITE_AUTH_MODE with no Entra ids configured
        # (this is a local dev build, so there are none) -- every local developer's `npm run
        # build` here would otherwise fail. Local dev is the intentional unauthenticated
        # pass-through case, so pin VITE_AUTH_MODE=Development unless the caller already set one
        # (an Entra dev box testing real sign-in, say), and restore whatever was there before.
        $prevMode = $env:VITE_AUTH_MODE
        if (-not $env:VITE_AUTH_MODE) { $env:VITE_AUTH_MODE = "Development" }
        try {
            npm install
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to restore frontend npm packages"
            }

            Write-Host ""
            Write-Host "Building frontend"
            Write-Host ""
            npm run build
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to build frontend"
            }
        }
        finally {
            $env:VITE_AUTH_MODE = $prevMode
            Pop-Location
        }
    }

    Write-Host ""
    Write-Host "Starting backend"
    Write-Host ""
    Push-Location "$repoRoot/app/backend"
    try {
        $venvPythonPath = Join-Path $repoRoot ".venv/scripts/python.exe"
        if ($IsLinux -or $IsMacOS) {
            $venvPythonPath = Join-Path $repoRoot ".venv/bin/python"
        }
        if ($Production) {
            # Production: gunicorn with aiohttp worker
            if (-not $env:HOST) { $env:HOST = "0.0.0.0" }
            if (-not $env:PORT) { $env:PORT = "8000" }
            if (-not $env:LOG_LEVEL) { $env:LOG_LEVEL = "info" }
            $env:RUNNING_IN_PRODUCTION = "true"
            Start-Process -FilePath $venvPythonPath -ArgumentList @(
                "-m", "gunicorn", "app:create_app",
                "-b", "$($env:HOST):$($env:PORT)",
                "--worker-class", "aiohttp.GunicornWebWorker",
                "--workers", "1",
                "--timeout", "120",
                "--keep-alive", "65",
                "--access-logfile", "-",
                "--log-level", $env:LOG_LEVEL
            ) -Wait -NoNewWindow
        } else {
            # Development: direct aiohttp
            Start-Process -FilePath $venvPythonPath -ArgumentList "-m app" -Wait -NoNewWindow
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start backend"
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    Pop-Location
}
