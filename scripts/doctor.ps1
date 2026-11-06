$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
try {
    $settings = Get-Content -LiteralPath (Join-Path $projectRoot 'config.local.json') -Raw | ConvertFrom-Json
    & $settings.python -X utf8 (Join-Path $PSScriptRoot 'doctor.py')
    exit $LASTEXITCODE
}
catch { Write-Error "Run install.cmd first. $_" -ErrorAction Continue; exit 1 }
