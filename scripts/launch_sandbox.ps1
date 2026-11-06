$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$statePath = Join-Path $projectRoot 'sandbox.local.json'
if (-not (Test-Path -LiteralPath $statePath)) { throw 'Game not linked. Run install.ps1 -GamePath <game folder> first.' }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$sandboxDirectory = Join-Path $projectRoot 'game-sandbox'
$exePath = Join-Path $sandboxDirectory '植物大战僵尸杂交版发布版0.31.0.Csharp.exe'
if ($state.gameDirectory -ne $sandboxDirectory) { throw 'The sandbox was moved. Run installation again in a fresh location.' }
$config = Get-Content -LiteralPath (Join-Path $sandboxDirectory 'override.cfg') -Raw
$userDataName = Split-Path -Leaf $state.userDataDirectory
if ($config -notmatch 'config/use_custom_user_dir=true' -or -not $config.Contains("config/custom_user_dir_name=`"$userDataName`"")) { throw 'Sandbox save override is missing; refusing to start.' }
$info = Get-Content -LiteralPath (Join-Path $projectRoot 'packages\build-info.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $sandboxDirectory 'data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll') -Algorithm SHA256).Hash -ne $info.gameAssemblySha256) { throw 'Sandbox game build has changed; reinstall a compatible bridge.' }
$existing = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath -eq $exePath }
if ($existing) { Write-Output "Sandbox already running: $($existing.ProcessId)"; return }
if (Get-NetTCPConnection -LocalPort 47632 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 47632 is already used by another game bridge. Close that game normally before starting this copy.' }
# start.cmd is the user's explicit request to launch a visible game window.
$process = Start-Process -FilePath $exePath -WorkingDirectory $sandboxDirectory -PassThru
Write-Output "Sandbox PID: $($process.Id). Enable AI 玩家接口 in the game Mod manager, then reload if prompted."
