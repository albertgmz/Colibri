# Root-run, dependency-free Windows helper checks. Never targets the real install/profile.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$helper = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\packaging\windows\install-helper.ps1'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('colibri-packaging-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
Set-Content -LiteralPath (Join-Path $testRoot 'packaging-test-root.marker') -Value 'isolated synthetic files'
$install = Join-Path $testRoot 'Install'
$payload = Join-Path $testRoot 'Payload'
$null = New-Item -ItemType Directory -Path $payload
Set-Content -LiteralPath (Join-Path $payload 'Colibri.exe') -Value 'synthetic executable bytes, never launched'
@{ format = 1; product = 'Colibri'; files = @(@{ path = 'Colibri.exe'; sha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'Colibri.exe')).Hash }) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'package-manifest.json')

function Invoke-Helper([string]$Action, [int]$Expected = 0, [string[]]$Extra = @()) {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $helper -Action $Action -TestRoot $testRoot @Extra
    if ($LASTEXITCODE -ne $Expected) { throw "Expected $Action exit $Expected, got $LASTEXITCODE" }
}
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }

try {
    $menu = Join-Path $testRoot 'StartMenu'
    $null = New-Item -ItemType Directory -Path $menu
    $collision = Join-Path $menu 'Colibri.lnk'
    Set-Content -LiteralPath $collision -Value 'user shortcut'
    $collisionHash = (Get-FileHash -LiteralPath $collision).Hash
    Invoke-Helper Check 1
    Assert-True ((Get-FileHash -LiteralPath $collision).Hash -eq $collisionHash) 'Unowned shortcut was changed.'
    Assert-True (-not (Test-Path -LiteralPath $install)) 'Preflight mutated the install directory.'
    Remove-Item -LiteralPath $collision

    Invoke-Helper Install 0 @('-Payload', $payload)
    Set-Content -LiteralPath (Join-Path $install 'Uninstall.exe') -Value 'synthetic uninstaller, never launched'
    $manifest = Join-Path $install 'installed-files.json'
    # Explicitly exercise replacement of an existing manifest through the Windows PowerShell 5.1 binder.
    Invoke-Helper Install 0 @('-Payload', $payload)
    $published = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    Assert-True ($published.product -eq 'Colibri' -and $published.files -contains 'Colibri.exe') 'Repeated publication lost ownership metadata.'
    Assert-True (@(Get-ChildItem -LiteralPath $install -Filter 'installed-files.json.*.tmp').Count -eq 0) 'Successful publication left a temporary manifest.'
    $prior = (Get-FileHash -LiteralPath $manifest).Hash
    Invoke-Helper Install 1 @('-Payload', $payload, '-FailManifestPublication')
    Assert-True ((Get-FileHash -LiteralPath $manifest).Hash -eq $prior) 'Failed publication destroyed the prior manifest.'
    $null = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    Invoke-Helper Install 0 @('-Payload', $payload)

    Invoke-Helper Shortcuts 0 @('-IncludeDesktop')
    Set-Content -LiteralPath $collision -Value 'user replacement shortcut'
    $replacementHash = (Get-FileHash -LiteralPath $collision).Hash
    $unowned = Join-Path $install 'user-note.txt'
    Set-Content -LiteralPath $unowned -Value 'preserve'
    $desktopShortcut = Join-Path $testRoot 'Desktop\Colibri.lnk'
    $lock = [IO.File]::Open($desktopShortcut, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Invoke-Helper RemoveExternal 1
        Assert-True (Test-Path -LiteralPath (Join-Path $install 'Uninstall.exe')) 'Cleanup failure lost the original uninstaller.'
        Assert-True (Test-Path -LiteralPath $manifest) 'Cleanup failure lost the retry manifest.'
    } finally { $lock.Dispose() }
    Invoke-Helper Remove
    Assert-True (Test-Path -LiteralPath $manifest) 'Binary removal finalized before external cleanup.'
    Invoke-Helper RemoveExternal
    Assert-True ((Get-FileHash -LiteralPath $collision).Hash -eq $replacementHash) 'Uninstall deleted a user replacement shortcut.'
    Invoke-Helper FinalizeRemove
    Assert-True (Test-Path -LiteralPath $unowned) 'Uninstall deleted an unowned file.'
    Assert-True (-not (Test-Path -LiteralPath $manifest)) 'Finalization retained the manifest.'
    Write-Host 'Packaging helper preservation/recovery checks passed.'
} finally {
    # Enumerate and remove only this verified, freshly created synthetic test directory.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('colibri-packaging-test-')) { throw 'Unsafe test cleanup path.' }
    Get-ChildItem -LiteralPath $resolved -File -Recurse -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Get-ChildItem -LiteralPath $resolved -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Remove-Item -LiteralPath $resolved
}
