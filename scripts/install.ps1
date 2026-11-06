param(
    [string]$GameDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'game-sandbox'),
    [string]$UserDataDirectory = '',
    [switch]$UpgradeWhenStopped,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $UserDataDirectory) {
    $state = Get-Content -LiteralPath (Join-Path $projectRoot 'sandbox.local.json') -Raw | ConvertFrom-Json
    if ($state.gameDirectory -ne [System.IO.Path]::GetFullPath($GameDirectory).TrimEnd('\', '/')) { throw 'Sandbox record does not match the target game directory.' }
    $UserDataDirectory = $state.userDataDirectory
}
$GameDirectory = [System.IO.Path]::GetFullPath($GameDirectory).TrimEnd('\', '/')
$UserDataDirectory = [System.IO.Path]::GetFullPath($UserDataDirectory).TrimEnd('\', '/')
$override = Get-Content -LiteralPath (Join-Path $GameDirectory 'override.cfg') -Raw
$userDataName = Split-Path -Leaf $UserDataDirectory
if ($UserDataDirectory -ne [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA $userDataName)).TrimEnd('\', '/') -or
    $override -notmatch 'config/use_custom_user_dir=true' -or
    -not $override.Contains("config/custom_user_dir_name=`"$userDataName`"")) { throw 'Target save override does not match the isolated Mod destination.' }
$info = Get-Content -LiteralPath (Join-Path $projectRoot 'packages\build-info.json') -Raw | ConvertFrom-Json
$packagePath = Join-Path (Join-Path $projectRoot 'packages') $info.packageName
$gameAssembly = Join-Path $GameDirectory 'data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll'
if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash -ne $info.gameAssemblySha256) {
    throw 'Game assembly differs from the build target. Rebuild and verify for this game before installing.'
}
if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $info.packageSha256) { throw 'Package hash mismatch.' }
$liveGame = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.Name -like '*.exe' -and
    [System.IO.Path]::GetFullPath((Split-Path -Parent $_.ExecutablePath)).TrimEnd('\', '/') -eq $GameDirectory
})
if ($CheckOnly) {
    [pscustomobject]@{ version = $info.bridgeVersion; package = $packagePath; package_sha256 = $info.packageSha256;
        game_directory = $GameDirectory; can_install = $liveGame.Count -eq 0; running_game_pids = @($liveGame | ForEach-Object { $_.ProcessId }) }
    return
}
if ($liveGame.Count -gt 0) { throw 'Exit this exact game process before installing or upgrading its Mod package.' }
$modsDirectory = Join-Path $UserDataDirectory 'Mods'
New-Item -ItemType Directory -Path $modsDirectory -Force | Out-Null
$destination = Join-Path $modsDirectory $info.packageName
if ($UpgradeWhenStopped) {
    $backupDir = Join-Path $projectRoot ('artifacts\mod-backup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    foreach ($old in Get-ChildItem -LiteralPath $modsDirectory -Filter 'pvz-ai-bridge-*.pmod' -File) {
        Move-Item -LiteralPath $old.FullName -Destination (Join-Path $backupDir $old.Name)
    }
}
if (Test-Path -LiteralPath $destination) {
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $info.packageSha256) {
        Write-Output 'The same package is already installed. Enable it from game Mod management when ready.'
        return
    }
    throw 'A different package already exists at the destination. Disable/uninstall the old version in game before replacing it.'
}
Copy-Item -LiteralPath $packagePath -Destination $destination
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $info.packageSha256) { throw 'Installed package hash mismatch.' }
Write-Output "Installed package: $destination"
Write-Output 'No running process or enabled-mods selection was changed. Existing enablement of this Mod ID is preserved.'
