param(
    [string]$GamePath = '',
    [ValidateSet('Auto','Codex','Generic')][string]$Client = 'Auto',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'PVZHybridAIBridge'),
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$installRoot = [IO.Path]::GetFullPath($InstallDir)
$temporaryRoot = $null
try {
    $installer = Join-Path $installRoot 'install.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        if ((Test-Path -LiteralPath $installRoot) -and
            (Get-ChildItem -LiteralPath $installRoot -Force | Select-Object -First 1)) {
            throw 'InstallDir is not empty and is not an existing bridge installation. Choose a new folder.'
        }
        $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('pvzhybrid-install-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        $archive = Join-Path $temporaryRoot 'source.zip'
        Write-Host 'Downloading PVZ Hybrid AI Bridge...'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/dy0000/pvz-hybrid-ai-bridge/archive/refs/heads/main.zip' -OutFile $archive
        $unpacked = Join-Path $temporaryRoot 'source'
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
        $sourceRoot = Join-Path $unpacked 'pvz-hybrid-ai-bridge-main'
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'install.ps1'))) { throw 'Downloaded archive is incomplete.' }
        New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $installRoot -Recurse
    }
    else { Write-Host 'Using existing installation. For source updates, follow the README update instructions.' }
    & $installer -GamePath $GamePath -Client $Client -NonInteractive:$NonInteractive
    exit $LASTEXITCODE
}
catch { Write-Error $_ -ErrorAction Continue; exit 1 }
finally {
    if ($temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $expectedPrefix = Join-Path ([IO.Path]::GetTempPath()) 'pvzhybrid-install-'
        if (-not $resolvedTemporaryRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean an unexpected temporary directory.'
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
