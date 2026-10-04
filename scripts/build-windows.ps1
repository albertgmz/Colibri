# Build and package serially. Each invocation owns a new directory beneath artifacts.
[CmdletBinding()]
param([string]$NsisPath = $env:COLIBRI_NSIS_PATH, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows packaging must run on Windows.' }
# Resolve script-module functions before a long build, rather than autoloading inside a nested pipeline.
foreach ($moduleName in @('Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Archive')) {
    # A .bat launched by PowerShell 7 can pass its PSModulePath to Windows PowerShell 5.1.
    $moduleManifest = Join-Path $PSHOME ("Modules\$moduleName\$moduleName.psd1")
    Import-Module -Name $moduleManifest -ErrorAction Stop
}
$hashCommand = Get-Command -Name Get-FileHash -Module Microsoft.PowerShell.Utility -ErrorAction Stop
$archiveCommand = Get-Command -Name Compress-Archive -Module Microsoft.PowerShell.Archive -ErrorAction Stop
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $NsisPath) { $NsisPath = Join-Path $repository 'artifacts\tools\nsis-3.13\makensis.exe' }
$NsisPath = [IO.Path]::GetFullPath($NsisPath)
if (-not (Test-Path -LiteralPath $NsisPath -PathType Leaf)) {
    throw 'Provide -NsisPath pointing to makensis.exe in a complete extracted NSIS portable distribution.'
}
$sourcePrefix = (Join-Path $repository 'src') + '\'
foreach ($process in @(Get-Process -Name Colibri,Colibri.NativeHost,aria2c -ErrorAction SilentlyContinue)) {
    if (-not $process.Path) { throw 'Cannot inspect a running download process. Stop before building shared development output.' }
    if ($process.Path.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A development executable is running from src. Exit it before building.'
    }
}
$runName = 'windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$artifactRoot = Join-Path $repository ('artifacts\' + $runName)
$portable = Join-Path $artifactRoot 'Colibri'
if (Test-Path -LiteralPath $artifactRoot) { throw 'Artifact directory already exists; refusing to overwrite it.' }
$null = New-Item -ItemType Directory -Path $portable

function Assert-Command([string]$Stage) {
    if ($LASTEXITCODE -ne 0) { throw "$Stage failed with exit code $LASTEXITCODE. Output is retained; no retry was attempted." }
}

Push-Location $repository
try {
    & dotnet build Colibri.slnx -c Release
    Assert-Command 'Build'
    & dotnet test Colibri.slnx -c Release --no-build
    Assert-Command 'Tests'
    & dotnet publish src/Colibri.App/Colibri.App.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishTrimmed=false -p:PublishAot=false -p:PublishReadyToRun=false `
        -p:DebugType=None -p:DebugSymbols=false -o $portable
    Assert-Command 'Publish'
    # Only files in this newly created output are eligible for symbol removal.
    Get-ChildItem -LiteralPath $portable -File -Recurse | Where-Object Extension -in '.pdb','.dbg','.r2rmap' |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    foreach ($required in @('Colibri.exe','Colibri.NativeHost.exe','Colibri.runtimeconfig.json','Colibri.deps.json',
            'Colibri.NativeHost.runtimeconfig.json','Colibri.NativeHost.deps.json','aria2\aria2c.exe','aria2\COPYING','aria2\LICENSE.OpenSSL')) {
        if (-not (Test-Path -LiteralPath (Join-Path $portable $required) -PathType Leaf)) { throw "Missing published dependency: $required" }
    }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $portable
    Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $portable
    Copy-Item -LiteralPath (Join-Path $repository 'third_party\aria2\README.md') -Destination (Join-Path $portable 'aria2\README.md')
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $portable 'Colibri.exe')).ProductVersion
    if (-not $version) { throw 'Published executable has no product version.' }
    $files = @(Get-ChildItem -LiteralPath $portable -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($portable.Length + 1); sha256 = (& $hashCommand -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ format = 1; product = 'Colibri'; version = $version; files = $files } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $portable 'package-manifest.json') -Encoding UTF8
    $zip = Join-Path $artifactRoot 'Colibri-win-x64-portable.zip'
    & $archiveCommand -LiteralPath $portable -DestinationPath $zip -CompressionLevel Optimal
    $installer = Join-Path $artifactRoot 'Colibri-win-x64-setup.exe'
    & $NsisPath /NOCONFIG "/DPAYLOAD_PATH=$portable" "/DINSTALLER_PATH=$installer" "/DPACKAGE_VERSION=$version" `
        "/DHELPER_PATH=$(Join-Path $repository 'packaging\windows\install-helper.ps1')" (Join-Path $repository 'packaging\windows\Colibri.nsi')
    Assert-Command 'NSIS compilation'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'NSIS did not produce an installer.' }
    @{ version = $version; portable = $portable; zip = $zip; installer = $installer } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $artifactRoot 'build-results.json') -Encoding UTF8
    Write-Host "Portable: $portable"
    Write-Host "ZIP: $zip"
    Write-Host "Installer: $installer"
    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $portable 'Colibri.exe') -WorkingDirectory $portable }
} finally { Pop-Location }
