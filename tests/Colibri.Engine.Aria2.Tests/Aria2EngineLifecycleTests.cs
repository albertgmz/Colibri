using System.Collections.Concurrent;
using Colibri.Core.Engine;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Engine.Aria2.Tests;

/// <summary>
/// Start and stop paths that need no real aria2. The full run against aria2 is the smoke test project.
/// </summary>
public sealed class Aria2EngineLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "colibri-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<EngineState> _states = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Aria2Engine CreateEngine(string? aria2Path)
    {
        var engine = new Aria2Engine(
            new FixedLocator(aria2Path),
            new TestPaths(_root),
            NullLogger<Aria2Engine>.Instance,
            () => new Aria2Settings(null, new EngineOptions(3, 16, 0)));
        engine.StateChanged += (_, state) => _states.Enqueue(state);
        return engine;
    }

    [Fact]
    public async Task Missing_aria2_gives_NotFound_without_throwing()
    {
        using var engine = CreateEngine(aria2Path: null);

        await engine.StartAsync(Ct);

        Assert.Equal(EngineState.NotFound, engine.State);
        Assert.Equal([EngineState.NotFound], _states);
    }

    [Fact]
    public async Task A_file_that_cannot_run_gives_Failed_and_a_later_start_tries_again()
    {
        Directory.CreateDirectory(_root);
        var notAProgram = Path.Combine(_root, "aria2c.txt");
        await File.WriteAllTextAsync(notAProgram, "not a program", Ct);
        using var engine = CreateEngine(notAProgram);

        await engine.StartAsync(Ct);
        await engine.StartAsync(Ct);

        Assert.Equal(EngineState.Failed, engine.State);
        Assert.Equal([EngineState.Starting, EngineState.Failed, EngineState.Starting, EngineState.Failed], _states);
    }

    [Fact]
    public async Task Unwritable_logs_folder_gives_Failed_and_does_not_block_later_starts()
    {
        Directory.CreateDirectory(_root);
        var notAProgram = Path.Combine(_root, "aria2c.txt");
        await File.WriteAllTextAsync(notAProgram, "not a program", Ct);

        // A file where the logs folder should be makes creating the folder fail.
        var blocker = Path.Combine(_root, "logs");
        await File.WriteAllTextAsync(blocker, "", Ct);
        using var engine = CreateEngine(notAProgram);

        await engine.StartAsync(Ct);
        Assert.Equal(EngineState.Failed, engine.State);

        File.Delete(blocker);
        await engine.StartAsync(Ct);

        Assert.Equal([EngineState.Starting, EngineState.Failed, EngineState.Starting, EngineState.Failed], _states);
        Assert.True(Directory.Exists(new TestPaths(_root).LogsDirectory));
    }

    [Fact]
    public async Task Config_file_holds_only_the_secret_and_is_owner_only_on_unix()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "aria2-rpc.conf");
        await File.WriteAllTextAsync(path, "stale content from a crash", Ct);

        Aria2Process.WriteConfigFile(path, "abc123");

        Assert.Equal("rpc-secret=abc123\n", await File.ReadAllTextAsync(path, Ct));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task Config_file_with_the_secret_does_not_stay_on_disk()
    {
        Directory.CreateDirectory(_root);
        var notAProgram = Path.Combine(_root, "aria2c.txt");
        await File.WriteAllTextAsync(notAProgram, "not a program", Ct);
        using var engine = CreateEngine(notAProgram);

        await engine.StartAsync(Ct);

        Assert.False(File.Exists(Path.Combine(_root, "aria2-rpc.conf")));
    }

    [Fact]
    public async Task Start_creates_logs_but_no_plaintext_session_file()
    {
        Directory.CreateDirectory(_root);
        var notAProgram = Path.Combine(_root, "aria2c.txt");
        await File.WriteAllTextAsync(notAProgram, "not a program", Ct);
        using var engine = CreateEngine(notAProgram);
        var paths = new TestPaths(_root);

        await engine.StartAsync(Ct);

        Assert.False(File.Exists(paths.Aria2SessionPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(paths.Aria2SessionPath)));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public async Task Engine_start_and_stop_preserve_legacy_session_until_explicit_protected_cleanup()
    {
        var paths = new TestPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Aria2SessionPath)!);
        await File.WriteAllTextAsync(paths.Aria2SessionPath, "Cookie: legacy-secret", Ct);
        await File.WriteAllTextAsync(paths.Aria2SessionPath + ".tmp", "Authorization: legacy-secret", Ct);
        var controlPath = Path.Combine(_root, "download.bin.aria2");
        await File.WriteAllTextAsync(controlPath, "partial control", Ct);
        var notAProgram = Path.Combine(_root, "aria2c.txt");
        await File.WriteAllTextAsync(notAProgram, "not a program", Ct);
        using var engine = CreateEngine(notAProgram);
        await engine.StartAsync(Ct);
        await engine.StopAsync(Ct);
        Assert.Equal("Cookie: legacy-secret", await File.ReadAllTextAsync(paths.Aria2SessionPath, Ct));

        await ((ILegacyCredentialCleanup)engine).CleanupAsync(Ct);
        Assert.False(File.Exists(paths.Aria2SessionPath));
        Assert.False(File.Exists(paths.Aria2SessionPath + ".tmp"));
        Assert.Equal("partial control", await File.ReadAllTextAsync(controlPath, Ct));
        await engine.CleanupAsync(Ct); // Missing legacy files are harmless.
    }

    [Fact]
    public async Task Canceled_cleanup_preserves_legacy_session()
    {
        var paths = new TestPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Aria2SessionPath)!);
        await File.WriteAllTextAsync(paths.Aria2SessionPath, "legacy", Ct);
        using var engine = CreateEngine(null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.CleanupAsync(cancellation.Token));
        Assert.True(File.Exists(paths.Aria2SessionPath));
    }

    [Fact]
    public async Task Cleanup_failure_propagates_to_prevent_starting_with_plaintext_leftovers()
    {
        var paths = new TestPaths(_root);
        Directory.CreateDirectory(paths.Aria2SessionPath); // File.Delete cannot remove a directory.
        using var engine = CreateEngine(null);
        await Assert.ThrowsAnyAsync<Exception>(() => engine.CleanupAsync(Ct));
        Assert.True(Directory.Exists(paths.Aria2SessionPath));
        Assert.Empty(_states);
    }

    [Fact]
    public async Task Stop_without_start_is_harmless_and_repeatable()
    {
        using var engine = CreateEngine(aria2Path: null);

        await engine.StopAsync(Ct);
        await engine.StopAsync(Ct);

        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Empty(_states);
    }

    [Fact]
    public async Task Calls_while_not_running_throw_EngineOperationException()
    {
        using var engine = CreateEngine(aria2Path: null);

        await Assert.ThrowsAsync<EngineOperationException>(() => engine.GetStatusAsync("aaaaaaaaaaaaaaaa", Ct));
        await Assert.ThrowsAsync<EngineOperationException>(() => engine.PauseAsync("aaaaaaaaaaaaaaaa", Ct));
    }

    private sealed class FixedLocator(string? path) : IAria2Locator
    {
        public string? FindAria2(string? configuredPath) => path;
    }

    private sealed class TestPaths(string root) : IAppPaths
    {
        public string DataDirectory => root;

        public string DatabasePath => Path.Combine(root, "colibri.db");

        public string SettingsPath => Path.Combine(root, "settings.json");

        public string LogsDirectory => Path.Combine(root, "logs");

        public string Aria2SessionPath => Path.Combine(root, "session", "aria2.session");

        public string DefaultDownloadsDirectory => Path.Combine(root, "downloads");
    }
}
