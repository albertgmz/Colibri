using System.Diagnostics;
using System.Text.Json;
using Colibri.Core.Updates;

namespace Colibri.Platform.Updates;

internal static class WindowsUpdateHandoff
{
    internal static bool IsOwnedInstallation(string executable, string installDirectory)
    {
        try
        {
            if (!Path.GetFullPath(executable).Equals(Path.Combine(Path.GetFullPath(installDirectory), "Colibri.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            var manifest = Path.Combine(installDirectory, "installed-files.json");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024) return false;
            using var json = JsonDocument.Parse(File.ReadAllBytes(manifest));
            var root = json.RootElement;
            return root.GetProperty("format").GetInt32() == 1 && root.GetProperty("product").GetString() == "Colibri" &&
                root.GetProperty("files").EnumerateArray().Any(item => item.GetString()?.Equals("Colibri.exe", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
    }

    internal static async Task PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        // A fixed script waits for the exact initiating process. Setup independently refuses any
        // surviving installed app/native host/aria2; shutdown completion is not treated as proof of exit.
        var script = Path.Combine(Path.GetDirectoryName(package.Path)!, "install-after-exit.ps1");
        var ready = Path.Combine(Path.GetDirectoryName(package.Path)!, "handoff-ready.txt");
        if (File.Exists(script) || File.Exists(ready)) throw new IOException("An update handoff was already prepared; download again.");
        await using (var file = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        await using (var writer = new StreamWriter(file)) await writer.WriteAsync(HelperScript.AsMemory(), cancellationToken).ConfigureAwait(false);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-AppProcessId", current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-StartTicks", current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-PackagePath", package.Path, "-ExpectedSize", package.Source.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-ExpectedHash", package.Source.Sha256 }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the update handoff.");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(ready))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (helper.HasExited || DateTime.UtcNow >= deadline) throw new IOException("The update helper did not become ready; Colibri will remain open.");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(package.Path)!, "handoff-armed.txt"), "install", cancellationToken).ConfigureAwait(false);
    }

    internal const string HelperScript = """
        param([int]$AppProcessId, [long]$StartTicks, [string]$PackagePath, [long]$ExpectedSize, [string]$ExpectedHash)
        $ErrorActionPreference = 'Stop'
        try {
            $appProcess = $null
            try { $appProcess = [Diagnostics.Process]::GetProcessById($AppProcessId) } catch [ArgumentException] { }
            if ($null -eq $appProcess) { throw 'Initiating process already exited before handoff.' }
            if ($null -ne $appProcess) {
                if ($appProcess.StartTime.ToUniversalTime().Ticks -ne $StartTicks) { throw 'Process identity changed.' }
                [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($PackagePath)) 'handoff-ready.txt'), 'ready')
                $armPath = Join-Path ([IO.Path]::GetDirectoryName($PackagePath)) 'handoff-armed.txt'
                $armDeadline = [DateTime]::UtcNow.AddSeconds(5)
                while (-not [IO.File]::Exists($armPath)) {
                    if ([DateTime]::UtcNow -ge $armDeadline) { throw 'Handoff was not authorized.' }
                    [Threading.Thread]::Sleep(50)
                }
                if (-not $appProcess.WaitForExit(120000)) { throw 'Colibri did not exit; update cancelled.' }
                $appProcess.Dispose()
            }
            # Hold a read-only file handle through launch so the verified bytes cannot be replaced.
            $packageStream = [IO.File]::Open($PackagePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($packageStream.Length -ne $ExpectedSize) { throw 'Update size changed.' }
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actual = [BitConverter]::ToString($sha.ComputeHash($packageStream)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
                if ($actual -ne $ExpectedHash) { throw 'Update integrity check failed.' }
                $installerStart = New-Object Diagnostics.ProcessStartInfo
                $installerStart.FileName = $PackagePath
                $installerStart.UseShellExecute = $true
                $installerProcess = [Diagnostics.Process]::Start($installerStart)
                if ($null -eq $installerProcess) { throw 'Installer did not start.' }
                $installerProcess.Dispose()
            } finally { $packageStream.Dispose() }
        } catch {
            [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($PackagePath)) 'handoff-error.txt'), 'Update handoff failed. Reopen Colibri and download the update again.')
            exit 1
        }
        """;
}
