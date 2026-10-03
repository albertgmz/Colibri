using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Colibri.Core.Engine;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Engine.Aria2;

/// <summary>
/// Runs aria2c as a hidden child process and keeps an RPC connection to it. Restarts aria2 when it
/// exits without being asked to, and gives up (<see cref="EngineState.Failed"/>) after several quick
/// failures in a row.
/// </summary>
/// <remarks>
/// After a restart the state goes Restarting -> Running with an empty transfer list. Core reconciles
/// its protected repository, re-adding stable GIDs against existing partial/control files.
/// Legacy plaintext sessions are never read, saved or deleted during engine launch.
/// </remarks>
internal sealed class Aria2Process : IDisposable
{
    // Wait before each restart attempt. When all are used up without aria2 staying up, give up.
    private static readonly TimeSpan[] RestartDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    // aria2 running at least this long counts as healthy and resets the failure count.
    private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);

    // aria2 needs a moment after launch before it accepts connections.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(100);

    private const int LaunchAttempts = 3;

    private readonly IAria2Locator _locator;
    private readonly IAppPaths _paths;
    private readonly ILogger _logger;
    private readonly Func<EngineOptions> _currentOptions;

    // Start, stop and restart run one at a time.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private string? _executable;
    private volatile Process? _process;
    private volatile Aria2RpcClient? _client;
    private CancellationTokenSource _stopCts = new();
    private volatile bool _stopRequested;
    private int _quickFailures;
    private volatile EngineState _state = EngineState.Stopped;

    // Dispose does not wait for the lifecycle lock (a restart can hold it for seconds), so storing and
    // releasing the process is guarded separately; a launch finishing during Dispose cannot leak aria2.
    private readonly object _processGate = new();
    private bool _disposed;

    public Aria2Process(IAria2Locator locator, IAppPaths paths, ILogger logger, Func<EngineOptions> currentOptions)
    {
        _locator = locator;
        _paths = paths;
        _logger = logger;
        _currentOptions = currentOptions;
    }

    public EngineState State => _state;

    /// <summary>Per-launch aria2 config file holding the RPC secret; it exists only while aria2 starts up.</summary>
    private string ConfigPath => Path.Combine(_paths.DataDirectory, "aria2-rpc.conf");

    /// <summary>The RPC connection to the running aria2, or null while it is not running.</summary>
    public Aria2RpcClient? Client => _client;

    /// <summary>Process id of the running aria2c, or null.</summary>
    public int? ProcessId => _process?.Id;

    public event EventHandler<EngineState>? StateChanged;

    /// <summary>Download notifications from whichever aria2 instance is currently running.</summary>
    public event EventHandler<EngineDownloadEvent>? DownloadEvent;

    /// <summary>
    /// Launches aria2 and connects to it. Does not throw when aria2 is missing or fails to start;
    /// the state becomes <see cref="EngineState.NotFound"/> or <see cref="EngineState.Failed"/> instead.
    /// </summary>
    public async Task StartAsync(string? configuredPath, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_state is EngineState.Running or EngineState.Starting)
            {
                return;
            }

            _stopRequested = false;
            _stopCts = new CancellationTokenSource();

            _executable = _locator.FindAria2(configuredPath);
            if (_executable is null)
            {
                _logger.LogError("aria2c was not found (configured path: {Path})", configuredPath);
                SetState(EngineState.NotFound);
                return;
            }

            SetState(EngineState.Starting);
            try
            {
                PrepareFiles();
                _quickFailures = 0;
                SetState(await TryLaunchAsync(ct) ? EngineState.Running : EngineState.Failed);
            }
            catch (OperationCanceledException)
            {
                SetState(EngineState.Stopped);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not create aria2's data or log folder");
                SetState(EngineState.Failed);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Shuts aria2 down: politely first, then forced, then by killing the
    /// process. Safe to call more than once. Not cancellable: a half-done stop would leave aria2 running
    /// with crash recovery switched off, and the steps have their own time limits.
    /// </summary>
    public async Task StopAsync()
    {
        _stopRequested = true;
        _stopCts.Cancel(); // Interrupts a restart that is waiting or connecting.

        await _lifecycleLock.WaitAsync();
        try
        {
            // Set again: a StartAsync that held the lock before us has cleared it.
            _stopRequested = true;
            if (_process is { } process)
            {
                await ShutDownAsync(process, _client);
                ReleaseProcess();
            }

            DeleteConfigFile();
            SetState(EngineState.Stopped);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _stopCts.Cancel();
        lock (_processGate)
        {
            _disposed = true;
            if (_process is { } process)
            {
                Kill(process);
            }

            ReleaseProcess();
        }

        DeleteConfigFile();
    }

    private async Task ShutDownAsync(Process process, Aria2RpcClient? client)
    {
        if (client is not null)
        {
            try
            {
                await client.ShutdownAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "aria2 did not accept a normal shutdown");
            }
        }

        // aria2.shutdown waits for running downloads to close their connections cleanly.
        if (await WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
        {
            return;
        }

        if (client is not null)
        {
            try
            {
                await client.ForceShutdownAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "aria2 did not accept a forced shutdown");
            }
        }

        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(2)))
        {
            _logger.LogWarning("aria2 did not exit; killing it");
            Kill(process);
        }
    }

    private void PrepareFiles()
    {
        Directory.CreateDirectory(_paths.DataDirectory);

        Directory.CreateDirectory(_paths.LogsDirectory);
    }

    /// <summary>Launches aria2 and connects. Must be called with the lifecycle lock held.</summary>
    private async Task<bool> TryLaunchAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LaunchAttempts; attempt++)
        {
            var port = FindFreePort();
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            WriteConfigFile(ConfigPath, secret);

            Process process;
            Stopwatch uptime;
            Aria2RpcClient? client;
            try
            {
                try
                {
                    process = StartProcess(port);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    _logger.LogError(ex, "Could not start aria2c at {Path}", _executable);
                    return false;
                }

                uptime = Stopwatch.StartNew();
                try
                {
                    client = await ConnectAsync(process, port, secret, ct);
                }
                catch
                {
                    Kill(process);
                    process.Dispose();
                    throw;
                }
            }
            finally
            {
                // aria2 reads its config file only at startup. Once it has answered (or failed), the secret
                // does not need to stay on disk.
                DeleteConfigFile();
            }

            if (client is not null)
            {
                bool stored;
                lock (_processGate)
                {
                    stored = !_disposed;
                    if (stored)
                    {
                        _process = process;
                        _client = client;
                    }
                }

                if (!stored)
                {
                    client.Dispose();
                    Kill(process);
                    process.Dispose();
                    return false;
                }

                _ = MonitorAsync(process, uptime);

                // A drop before the client was stored was ignored by OnDisconnected; handle it now.
                if (client.IsClosed)
                {
                    OnDisconnected(process);
                }

                return true;
            }

            if (!process.HasExited)
            {
                _logger.LogError("aria2 started but did not accept an RPC connection within {Timeout}", ConnectTimeout);
                Kill(process);
                process.Dispose();
                return false;
            }

            // The usual cause is the port race described in FindFreePort, so try again with a new port.
            _logger.LogWarning("aria2 exited during startup with code {ExitCode} (attempt {Attempt} of {Attempts})",
                process.ExitCode, attempt, LaunchAttempts);
            process.Dispose();
        }

        return false;
    }

    private Process StartProcess(int port)
    {
        var startInfo = new ProcessStartInfo(_executable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var arguments = Aria2Arguments.Build(
            port,
            ConfigPath,
            Environment.ProcessId,
            Path.Combine(_paths.LogsDirectory, "aria2.log"),
            _currentOptions());
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        // Redirected output must be read continuously: if nobody reads it, the pipe buffer fills up and
        // aria2 blocks on its next write. BeginOutputReadLine/BeginErrorReadLine read it in the background.
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogInformation("aria2: {Line}", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogWarning("aria2: {Line}", e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Writes the per-launch config file with the RPC secret, readable only by the current user.</summary>
    internal static void WriteConfigFile(string path, string secret)
    {
        // CreateNew must really create the file, otherwise the permissions below would not apply.
        File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        // Unix: the file is created owner-only (0600) before the secret is written. Windows: the per-user
        // data folder (under LocalAppData) is already closed to other users by its ACL.
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var writer = new StreamWriter(path, options);
        writer.Write($"rpc-secret={secret}\n"); // "\n", not WriteLine: a Windows "\r" could end up in the secret.
    }

    private void DeleteConfigFile()
    {
        try
        {
            File.Delete(ConfigPath);
        }
        catch (DirectoryNotFoundException)
        {
            // The data folder itself is gone, so there is nothing to delete.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the aria2 config file {Path}", ConfigPath);
        }
    }

    /// <summary>
    /// Connects to a freshly started aria2, retrying while it is still starting up. Returns null if the
    /// process exits or does not answer in time.
    /// </summary>
    private async Task<Aria2RpcClient?> ConnectAsync(Process process, int port, string secret, CancellationToken ct)
    {
        var uri = new Uri($"ws://127.0.0.1:{port}/jsonrpc");
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < ConnectTimeout && !process.HasExited)
        {
            Aria2RpcClient? client = null;
            try
            {
                var transport = await Aria2WebSocketTransport.ConnectAsync(uri, ct);
                client = new Aria2RpcClient(transport, secret, logger: _logger);
                client.Notification += (_, e) => DownloadEvent?.Invoke(this, e);
                client.Disconnected += (_, _) => OnDisconnected(process);
                client.Start();

                var version = await client.GetVersionAsync(ct);
                _logger.LogInformation("aria2 {Version} is running (process {ProcessId}, port {Port})",
                    version, process.Id, port);
                return client;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                // Not listening yet; try again shortly.
                _logger.LogDebug(ex, "aria2 is not accepting connections yet");
            }

            await Task.Delay(ConnectRetryDelay, ct);
        }

        return null;
    }

    /// <summary>Waits for an aria2 process to exit and restarts it if nobody asked it to stop.</summary>
    private async Task MonitorAsync(Process process, Stopwatch uptime)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return; // Released by StopAsync or Dispose meanwhile.
        }

        if (_stopRequested)
        {
            return;
        }

        await _lifecycleLock.WaitAsync();
        try
        {
            if (_stopRequested || process != _process)
            {
                return;
            }

            _logger.LogWarning("aria2 exited unexpectedly with code {ExitCode} after {Uptime}", process.ExitCode, uptime.Elapsed);
            ReleaseProcess();

            if (uptime.Elapsed >= StableUptime)
            {
                _quickFailures = 0;
            }

            await RestartAsync(_stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // StopAsync interrupted the restart; it sets the final state.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restarting aria2 failed");
            SetState(EngineState.Failed);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task RestartAsync(CancellationToken ct)
    {
        while (_quickFailures < RestartDelays.Length)
        {
            SetState(EngineState.Restarting);
            await Task.Delay(RestartDelays[_quickFailures], ct);
            _quickFailures++;

            if (await TryLaunchAsync(ct))
            {
                SetState(EngineState.Running);
                return;
            }
        }

        _logger.LogError("aria2 failed {Count} times in a row; giving up", _quickFailures);
        SetState(EngineState.Failed);
    }

    private void OnDisconnected(Process process)
    {
        // The connection dropped while aria2 is still running. Without a connection aria2 is useless,
        // so kill it and let MonitorAsync start a fresh one.
        // Under the gate so the process cannot be released (disposed) while it is checked and killed.
        lock (_processGate)
        {
            if (_stopRequested || process != _process || process.HasExited)
            {
                return;
            }

            _logger.LogWarning("Lost the connection to aria2; restarting it");
            Kill(process);
        }
    }

    private void ReleaseProcess()
    {
        lock (_processGate)
        {
            _client?.Dispose();
            _client = null;
            _process?.Dispose();
            _process = null;
        }
    }

    private void SetState(EngineState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A handler for the engine state change to {State} failed", state);
        }
    }

    private void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill aria2");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the OS for a free loopback port. There is a small race: another program could take the
    /// port between this check and aria2 binding it. aria2 then exits at once ("Failed to bind"),
    /// and <see cref="TryLaunchAsync"/> retries with a new port.
    /// </summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
