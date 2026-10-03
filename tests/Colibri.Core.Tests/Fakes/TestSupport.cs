using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Core.Tests.Fakes;

/// <summary>A clock that only moves when the test says so. Timers still use real time.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>All paths under one temporary folder, which is deleted on dispose.</summary>
public sealed class TempAppPaths : IAppPaths, IDisposable
{
    public TempAppPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "colibri-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "colibri.db");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string Aria2SessionPath => Path.Combine(DataDirectory, "aria2.session");

    public string DefaultDownloadsDirectory => Path.Combine(DataDirectory, "Downloads");

    public void Dispose()
    {
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be open on Windows; the temp folder is cleaned up eventually.
        }
    }
}

/// <summary>Logger that keeps the messages, so tests can check what was logged.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
