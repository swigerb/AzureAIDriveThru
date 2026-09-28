# Thin wrapper -- see scripts/download_local_models.py for the actual logic and full docs.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
python "$ScriptDir\download_local_models.py" @args
