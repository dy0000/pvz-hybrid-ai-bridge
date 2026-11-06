param([Parameter(Mandatory=$true)][string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
$sandboxDirectory = Join-Path $projectRoot 'game-sandbox'
$statePath = Join-Path $projectRoot 'sandbox.local.json'
$exeName = '植物大战僵尸杂交版发布版0.31.0.Csharp.exe'
$info = Get-Content -LiteralPath (Join-Path $projectRoot 'packages\build-info.json') -Raw | ConvertFrom-Json
$gameAssembly = Join-Path $SourceDirectory 'data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll'
foreach ($relative in @($exeName, [IO.Path]::ChangeExtension($exeName, '.pck'), 'data_PlantsVsZombies_windows_x86_64\GodotSharp.dll', 'data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $relative) -PathType Leaf)) { throw "Missing game file: $relative. Extract the complete Windows 0.31.0 C# game." }
}
if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash -ne $info.gameAssemblySha256) {
    throw 'This game build is not compatible with the packaged bridge. Supported build: Hybrid Remastered 0.31.0 (C#).'
}
# Each installation has its own new save directory.
$hasher = [Security.Cryptography.SHA256]::Create()
try { $rootHash = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($projectRoot.ToLowerInvariant())))).Replace('-', '').Substring(0, 12).ToLowerInvariant() }
finally { $hasher.Dispose() }
$userDataName = "PVZ-Hybrid-AI-$rootHash"
$sandboxUserData = Join-Path $env:APPDATA $userDataName
if (Test-Path -LiteralPath $sandboxDirectory) {
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'Existing game-sandbox has no installation record. Refusing to merge or overwrite it.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.gameDirectory -ne $sandboxDirectory -or $state.userDataDirectory -ne $sandboxUserData) { throw 'Sandbox record belongs to a different installation. Refusing to reuse it.' }
    $override = Get-Content -LiteralPath (Join-Path $sandboxDirectory 'override.cfg') -Raw
    if ($override -notmatch 'config/use_custom_user_dir=true' -or -not $override.Contains("config/custom_user_dir_name=`"$userDataName`"")) { throw 'Sandbox save override is missing or changed. Refusing to reuse it.' }
    Write-Output 'Using this installation sandbox and its existing saves.'
    return
}
if ($sandboxDirectory.StartsWith($SourceDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The project sandbox cannot be nested inside the source game directory.' }
if (Test-Path -LiteralPath $sandboxUserData) { throw "Save directory already exists without this installation record; refusing to adopt it: $sandboxUserData" }
New-Item -ItemType Directory -Path $sandboxDirectory | Out-Null
Get-ChildItem -LiteralPath $SourceDirectory -Force | Copy-Item -Destination $sandboxDirectory -Recurse
@"
; Isolated AI copy. The original game and original saves are not changed.
[application]
config/use_custom_user_dir=true
config/custom_user_dir_name="$userDataName"

[pvz_ai]
instance="sandbox"
port=47632
local_decisions_enabled=false
in_game_panel_enabled=false
"@ | Set-Content -LiteralPath (Join-Path $sandboxDirectory 'override.cfg') -Encoding utf8
$verified = @()
foreach ($relative in @($exeName, [IO.Path]::ChangeExtension($exeName, '.pck'), 'data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll')) {
    $originalHash = (Get-FileHash -LiteralPath (Join-Path $SourceDirectory $relative) -Algorithm SHA256).Hash
    $copyHash = (Get-FileHash -LiteralPath (Join-Path $sandboxDirectory $relative) -Algorithm SHA256).Hash
    if ($copyHash -ne $originalHash) { throw "Copy verification failed: $relative" }
    $verified += @{ path = $relative; sha256 = $copyHash }
}
$state = [ordered]@{ gameDirectory=$sandboxDirectory; userDataDirectory=$sandboxUserData; port=47632; copiedOriginalSaves=$false; verified=$verified }
New-Item -ItemType Directory -Path $sandboxUserData | Out-Null
$state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot 'sandbox.local.json') -Encoding utf8
Write-Output "Created sandbox: $sandboxDirectory"
Write-Output "Created new save directory: $sandboxUserData"
