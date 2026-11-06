param(
    [string]$GamePath = '',
    [ValidateSet('Auto','Codex','Generic')][string]$Client = 'Auto',
    [string]$PythonPath = '',
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$projectRoot = $PSScriptRoot

function Find-Python312 {
    $candidates = @()
    if ($PythonPath) { $candidates += $PythonPath }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe')
    foreach ($key in @('HKCU:\Software\Python\PythonCore\3.12\InstallPath',
                       'HKLM:\Software\Python\PythonCore\3.12\InstallPath')) {
        if (Test-Path $key) { $candidates += (Join-Path (Get-Item $key).GetValue('') 'python.exe') }
    }
    $command = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source -notlike '*\WindowsApps\*') { $candidates += $command.Source }
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        $located = & $launcher.Source -3.12 -c 'import sys; print(sys.executable)' 2>$null
        if ($LASTEXITCODE -eq 0) { $candidates += $located }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        & $candidate -c 'import sys,struct; sys.exit(0 if sys.version_info[:2]==(3,12) and struct.calcsize(chr(80))==8 else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { return $candidate }
    }
    return $null
}

try {
    Write-Host 'PVZ Hybrid AI Bridge - preparing your AI game bridge'
    $selectedPython = Find-Python312
    if (-not $selectedPython) {
        if ($PythonPath) { throw '-PythonPath must point to 64-bit Python 3.12.' }
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) { throw 'Install Python 3.12 x64 from https://www.python.org/downloads/windows/ and run install.cmd again.' }
        Write-Host 'Installing Python 3.12 for the current user through winget...'
        & $winget.Source install --id Python.Python.3.12 --exact --source winget --scope user --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) { throw 'Python installation failed. Install Python 3.12 x64 manually and retry.' }
        $selectedPython = Find-Python312
        if (-not $selectedPython) { throw 'Python was not found after installation. Reopen the terminal and retry.' }
    }
    $sandbox = Join-Path $projectRoot 'game-sandbox'
    if (-not $GamePath -and (Test-Path -LiteralPath (Join-Path $projectRoot 'sandbox.local.json'))) {
        $GamePath = $sandbox
    }
    if (-not $GamePath) {
        $runningGames = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ExecutablePath -and $_.Name -like '*.Csharp.exe' -and
            (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $_.ExecutablePath) 'data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll'))
        } | Select-Object -ExpandProperty ExecutablePath -Unique)
        if ($runningGames.Count -eq 1) { $GamePath = $runningGames[0] }
    }
    if ($GamePath) {
        if (-not (Test-Path -LiteralPath $GamePath)) { throw "Game path does not exist: $GamePath" }
        $source = (Get-Item -LiteralPath $GamePath).FullName
        if (Test-Path -LiteralPath $source -PathType Leaf) { $source = Split-Path -Parent $source }
        & (Join-Path $projectRoot 'scripts\setup_sandbox.ps1') -SourceDirectory $source
        & (Join-Path $projectRoot 'scripts\install.ps1') -UpgradeWhenStopped
    }
    else { Write-Host 'No game selected. MCP setup will finish; later run install.ps1 -GamePath <your game folder>.' }
    & $selectedPython -X utf8 (Join-Path $projectRoot 'scripts\install.py')
    if ($LASTEXITCODE -ne 0) { throw 'MCP setup failed. No game controller was started.' }
    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($Client -eq 'Codex' -or ($Client -eq 'Auto' -and $codex)) {
        if (-not $codex) { throw 'Codex CLI was not found. Use -Client Generic and import generated\mcp.json manually.' }
        & $codex.Source mcp add pvz_hybrid -- $selectedPython -X utf8 (Join-Path $projectRoot 'scripts\run_mcp.py')
        if ($LASTEXITCODE -ne 0) { throw 'MCP registration failed. Import generated\mcp.json manually or retry -Client Codex.' }
        Write-Host 'Registered pvz_hybrid with Codex. Restart your AI client to load it.'
    }
    else { Write-Host 'Import generated\mcp.json into your AI client (stdio MCP).' }
    Write-Host 'Setup complete. Use start.cmd, enable AI 玩家接口 in Mod management, then give your AI generated\START_HERE.txt.'
    exit 0
}
catch { Write-Error $_ -ErrorAction Continue; exit 1 }
