# Runs only against the fixed per-user binary directory, never the Colibri data directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Check', 'Install', 'Shortcuts', 'RemoveExternal', 'Remove', 'FinalizeRemove')][string]$Action,
    [string]$Payload,
    [switch]$IncludeDesktop,
    [string]$TestRoot,
    [switch]$FailManifestPublication
)
$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Colibri'))
$manifestName = 'installed-files.json'
$shortcutManifestName = 'installed-shortcuts.json'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Colibri'
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
if ($TestRoot) {
    $TestRoot = [IO.Path]::GetFullPath($TestRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $TestRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($TestRoot)).StartsWith('colibri-packaging-test-') -or
        -not (Test-Path -LiteralPath (Join-Path $TestRoot 'packaging-test-root.marker') -PathType Leaf)) { throw 'Invalid isolated test root.' }
    $installRoot = Join-Path $TestRoot 'Install'
    $startMenu = Join-Path $TestRoot 'StartMenu'
    $desktop = Join-Path $TestRoot 'Desktop'
} elseif ($FailManifestPublication) { throw 'Failure injection requires an isolated test root.' }
$shortcutPaths = @{
    app = Join-Path $startMenu 'Colibri.lnk'
    uninstall = Join-Path $startMenu 'Uninstall Colibri.lnk'
    desktop = Join-Path $desktop 'Colibri.lnk'
}

function Assert-NoReparsePoint([string]$Path) {
    $ancestor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation contains a junction or symbolic link.' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

function Write-AtomicManifest([string]$Name, $Value) {
    $destination = Resolve-OwnedPath $Name
    $temporary = Resolve-OwnedPath ($Name + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $json = $Value | ConvertTo-Json -Depth 6
    $null = $json | ConvertFrom-Json
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        $null = Get-Content -LiteralPath $temporary -Raw | ConvertFrom-Json
        if ($FailManifestPublication) { throw 'Injected manifest publication failure.' }
        # Windows PowerShell 5.1 coerces ordinary $null to an empty string for this .NET string argument.
        if (Test-Path -LiteralPath $destination) {
            [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value)
        }
        else { [IO.File]::Move($temporary, $destination) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}

function Read-ShortcutOwnership {
    $path = Resolve-OwnedPath $shortcutManifestName
    $result = @{}
    if (Test-Path -LiteralPath $path) {
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($manifest.format -ne 1 -or $manifest.product -ne 'Colibri') { throw 'Invalid shortcut ownership manifest.' }
        foreach ($entry in $manifest.shortcuts) {
            if (-not $shortcutPaths.ContainsKey($entry.id) -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid shortcut ownership entry.' }
            $result[$entry.id] = $entry.sha256
        }
    }
    return $result
}

function Assert-ShortcutOwnership($Ownership, [string[]]$Ids) {
    foreach ($id in $Ids) {
        $path = $shortcutPaths[$id]
        Assert-NoReparsePoint $path
        if (Test-Path -LiteralPath $path) {
            if (-not $Ownership.ContainsKey($id) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Ownership[$id]) {
                throw "Refusing to replace an unowned or changed shortcut: $path"
            }
        }
    }
}

function Resolve-OwnedPath([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.IndexOfAny([char[]]':*?"<>|') -ge 0 -or ($Relative -split '[\\/]') -contains '..' -or
        ($Relative -split '[\\/]') -contains '.') { throw "Invalid installed file path." }
    $resolved = [IO.Path]::GetFullPath((Join-Path $installRoot $Relative))
    if (-not $resolved.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installed path escapes the binary directory.' }
    $ancestor = $resolved
    while (-not [string]::IsNullOrEmpty($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Installation contains a junction or symbolic link.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $resolved
}

function Read-OwnedFiles {
    $path = Resolve-OwnedPath $manifestName
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.product -ne 'Colibri' -or -not $manifest.files) { throw 'Invalid installation ownership manifest.' }
    foreach ($relative in $manifest.files) { $null = Resolve-OwnedPath $relative }
    return @($manifest.files)
}

function Assert-Unlocked($Files) {
    foreach ($relative in $Files) {
        $path = Resolve-OwnedPath $relative
        if (Test-Path -LiteralPath $path) {
            $handle = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $handle.Dispose()
        }
    }
}

function Assert-NotRunning {
    $null = Resolve-OwnedPath 'Colibri.exe'
    foreach ($process in @(Get-Process -Name Colibri,Colibri.NativeHost,aria2c -ErrorAction SilentlyContinue)) {
        $path = $process.Path
        if ([string]::IsNullOrWhiteSpace($path)) { throw 'Cannot inspect a running download process. Close it before installing.' }
        if ($path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Colibri is running from the installed directory. Exit Colibri from its tray menu and retry setup.'
        }
    }
}

try {
    Assert-NotRunning
    $owned = @(Read-OwnedFiles)
    Assert-Unlocked $owned
    $shortcuts = Read-ShortcutOwnership
    $requestedShortcuts = @('app', 'uninstall')
    if ($IncludeDesktop) { $requestedShortcuts += 'desktop' }
    if ($Action -eq 'Check') { Assert-ShortcutOwnership $shortcuts $requestedShortcuts; exit 0 }
    if ($Action -eq 'Shortcuts') {
        Assert-ShortcutOwnership $shortcuts $requestedShortcuts
        foreach ($id in $requestedShortcuts) {
            $path = $shortcutPaths[$id]
            if (Test-Path -LiteralPath $path) { continue }
            $temporary = Resolve-OwnedPath ('shortcut-' + [Guid]::NewGuid().ToString('N') + '.lnk')
            try {
                $shell = New-Object -ComObject WScript.Shell
                $shortcut = $shell.CreateShortcut($temporary)
                $shortcut.TargetPath = Resolve-OwnedPath $(if ($id -eq 'uninstall') { 'Uninstall.exe' } else { 'Colibri.exe' })
                $shortcut.WorkingDirectory = $installRoot
                $shortcut.Save()
                $shortcuts[$id] = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
                $entries = @($shortcuts.Keys | Sort-Object | ForEach-Object { @{ id = $_; sha256 = $shortcuts[$_] } })
                Write-AtomicManifest $shortcutManifestName @{ format = 1; product = 'Colibri'; shortcuts = $entries }
                Assert-NoReparsePoint $path
                $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force
                # No overwrite even if another process creates a shortcut after the preflight.
                [IO.File]::Copy($temporary, $path, $false)
            } finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
            }
        }
        exit 0
    }
    if ($Action -eq 'RemoveExternal') {
        foreach ($id in $shortcuts.Keys) {
            $path = $shortcutPaths[$id]
            Assert-NoReparsePoint $path
            if ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $shortcuts[$id]) {
                Remove-Item -LiteralPath $path
            }
            # A user replacement is left untouched, even though its name is the same.
        }
        if ((Test-Path -LiteralPath $startMenu) -and @(Get-ChildItem -LiteralPath $startMenu -Force).Count -eq 0) { Remove-Item -LiteralPath $startMenu }
        exit 0
    }
    if ($Action -eq 'FinalizeRemove') {
        if ($owned.Count -eq 0) { throw 'No ownership manifest exists; refusing final removal.' }
        foreach ($relative in @('Uninstall.exe', $shortcutManifestName, $manifestName)) {
            if ($owned -contains $relative) {
                $path = Resolve-OwnedPath $relative
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            }
        }
        if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $installRoot }
        exit 0
    }
    if ($Action -eq 'Remove') {
        if ($owned.Count -eq 0) { throw 'No ownership manifest exists; refusing to remove files.' }
        $directories = @()
        foreach ($relative in $owned) {
            if ($relative -in @($manifestName, $shortcutManifestName, 'Uninstall.exe')) { continue }
            $path = Resolve-OwnedPath $relative
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            $directory = [IO.Path]::GetDirectoryName($path)
            while ($directory.Length -gt $installRoot.Length) {
                $directories += $directory
                $directory = [IO.Path]::GetDirectoryName($directory)
            }
        }
        foreach ($directory in @($directories | Sort-Object -Unique | Sort-Object Length -Descending)) {
            if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $directory
            }
        }
        # Keep the manifest and original uninstaller until external cleanup succeeds.
        exit 0
    }
    if (-not $Payload) { throw 'Missing installation payload.' }
    $package = Get-Content -LiteralPath (Join-Path $Payload 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($package.format -ne 1 -or $package.product -ne 'Colibri' -or -not $package.files) { throw 'Invalid package manifest.' }
    $newFiles = @($package.files | ForEach-Object { $_.path }) + @('package-manifest.json', 'Uninstall.exe', $manifestName, $shortcutManifestName)
    if (@($newFiles | Group-Object | Where-Object Count -gt 1).Count) { throw 'Duplicate package path.' }
    foreach ($relative in $newFiles) {
        $target = Resolve-OwnedPath $relative
        if ((Test-Path -LiteralPath $target) -and $owned -notcontains $relative) { throw "Refusing to replace an unowned file: $relative" }
    }
    foreach ($entry in $package.files) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $Payload $entry.path) -Algorithm SHA256).Hash
        if ($actual -ne $entry.sha256) { throw "Package checksum mismatch: $($entry.path)" }
    }
    Assert-Unlocked $newFiles
    $null = New-Item -ItemType Directory -Path $installRoot -Force
    # Record both generations first so an interrupted copy remains repairable and uninstallable.
    $recoveryFiles = @(@($owned) + $newFiles | Sort-Object -Unique)
    Write-AtomicManifest $manifestName @{ format = 1; product = 'Colibri'; files = $recoveryFiles }
    foreach ($relative in @($package.files | ForEach-Object { $_.path }) + @('package-manifest.json')) {
        $target = Resolve-OwnedPath $relative
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath (Join-Path $Payload $relative) -Destination $target -Force
    }
    foreach ($relative in $owned) {
        if ($newFiles -notcontains $relative) {
            $path = Resolve-OwnedPath $relative
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
    }
    Write-AtomicManifest $manifestName @{ format = 1; product = 'Colibri'; files = $newFiles }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
