# Downloads only the pinned official NSIS archive; never runs the installer.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tools = Join-Path $repository 'artifacts\tools'
$destination = Join-Path $tools 'nsis-3.13'
$compiler = Join-Path $destination 'makensis.exe'
$expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
if (Test-Path -LiteralPath $compiler -PathType Leaf) { Write-Output $compiler; return }
if (Test-Path -LiteralPath $destination) { throw 'Incomplete NSIS directory exists; refusing to replace it. Supply -NsisPath or inspect it manually.' }
$staging = Join-Path $tools ('nsis-download-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $staging
$archive = Join-Path $staging 'nsis-3.13.zip'
$uri = [Uri]'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$deadline = [Diagnostics.Stopwatch]::StartNew()
for ($redirect = 0; $redirect -le 8; $redirect++) {
    if ($deadline.Elapsed.TotalSeconds -gt 120) { throw 'NSIS download deadline exceeded.' }
    if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Port -ne 443 -or
        ($uri.Host -ne 'sourceforge.net' -and -not $uri.Host.EndsWith('.sourceforge.net', [StringComparison]::OrdinalIgnoreCase))) { throw 'Unexpected NSIS download origin.' }
    $request = [Net.HttpWebRequest]::Create($uri)
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.UserAgent = 'Colibri-build/1'
    $response = $request.GetResponse()
    try {
        $status = [int]$response.StatusCode
        if ($status -in @(301,302,303,307,308)) {
            if ($redirect -eq 8 -or -not $response.Headers['Location']) { throw 'NSIS redirect limit exceeded.' }
            $uri = [Uri]::new($uri, $response.Headers['Location'])
            continue
        }
        if ($status -ne 200 -or $response.ContentLength -gt 16777216) { throw 'Unexpected NSIS response.' }
        $inputStream = $response.GetResponseStream()
        $outputStream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = New-Object byte[] 65536
            $total = 0
            while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($deadline.Elapsed.TotalSeconds -gt 120) { throw 'NSIS download deadline exceeded.' }
                $total += $count
                if ($total -gt 16777216) { throw 'NSIS archive exceeds the size limit.' }
                $outputStream.Write($buffer, 0, $count)
            }
            $outputStream.Flush($true)
        } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        break
    } finally { $response.Dispose() }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Pinned NSIS archive checksum mismatch; nothing was extracted.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
$extract = Join-Path $staging 'extracted'
try {
    foreach ($entry in $zip.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $extract $entry.FullName))
        if (-not $target.StartsWith($extract + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry escapes extraction directory.' }
    }
} finally { $zip.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $extract)
$source = Join-Path $extract 'nsis-3.13'
if (-not (Test-Path -LiteralPath (Join-Path $source 'makensis.exe') -PathType Leaf)) { throw 'Pinned archive lacks the expected compiler.' }
# Neither an existing tool distribution nor its executable is replaced.
[IO.Directory]::Move($source, $destination)
Write-Output $compiler
