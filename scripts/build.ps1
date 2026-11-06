param(
    [string]$GameDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'game-sandbox'),
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$gameData = Join-Path $GameDirectory 'data_PlantsVsZombies_windows_x86_64'
if (-not (Test-Path -LiteralPath (Join-Path $gameData 'PlantsVsZombies.dll'))) { throw 'Game runtime directory not found.' }
& $dotnetPath build (Join-Path $projectRoot 'src\PvzAiBridge\PvzAiBridge.csproj') -c Release --nologo "-p:GameDirectory=$GameDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Bridge build failed.' }
$assemblyPath = Join-Path $projectRoot 'src\PvzAiBridge\bin\Release\net9.0\ModAssembly.dll'
if ($Verify) {
    & $dotnetPath build (Join-Path $projectRoot 'tests\Bridge.Tests\Bridge.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests build failed.' }
    & $dotnetPath (Join-Path $projectRoot 'tests\Bridge.Tests\bin\Release\net9.0\Bridge.Tests.dll') $assemblyPath $gameData
    if ($LASTEXITCODE -ne 0) { throw 'Offline/compatibility verification failed.' }
}
$distDir = Join-Path $projectRoot 'packages'
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'mod\mod.json') -Raw | ConvertFrom-Json
$packageName = "pvz-ai-bridge-$($manifest.version).pmod"
$packagePath = Join-Path $distDir $packageName
$packageTemp = "$packagePath.tmp"
Add-Type -AssemblyName System.IO.Compression
$fileStream = [System.IO.File]::Open($packageTemp, [System.IO.FileMode]::Create)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{
            'mod.json' = (Join-Path $projectRoot 'mod\mod.json')
            'Runtime/ModAssembly.dll' = $assemblyPath
        }
        foreach ($entry in $entries.GetEnumerator()) {
            $zipEntry = $archive.CreateEntry($entry.Key)
            $outputStream = $zipEntry.Open()
            try { $bytes = [System.IO.File]::ReadAllBytes($entry.Value); $outputStream.Write($bytes, 0, $bytes.Length) }
            finally { $outputStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $fileStream.Dispose() }
Move-Item -LiteralPath $packageTemp -Destination $packagePath -Force
$metadata = [ordered]@{
    bridgeVersion = $manifest.version
    packageName = $packageName
    gameVersion = '0.31.0'
    gameAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $gameData 'PlantsVsZombies.dll') -Algorithm SHA256).Hash
    packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    verification = 'offline; live loading and gameplay still require in-game verification'
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $distDir 'build-info.json') -Encoding utf8
Write-Output "Package: $packagePath"
