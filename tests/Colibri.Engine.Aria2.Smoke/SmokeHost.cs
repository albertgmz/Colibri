using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Engine.Aria2.Smoke;

/// <summary>Keeps all of the smoke test's data in one temporary folder.</summary>
internal sealed class SmokePaths(string root) : IAppPaths
{
    public string DataDirectory => root;

    public string DatabasePath => Path.Combine(root, "colibri.db");

    public string SettingsPath => Path.Combine(root, "settings.json");

    public string LogsDirectory => Path.Combine(root, "logs");

    public string Aria2SessionPath => Path.Combine(root, "aria2.session");

    public string DefaultDownloadsDirectory => Path.Combine(root, "downloads");
}

/// <summary>
/// Finds the aria2c bundled in third_party on Windows, or aria2c on the PATH elsewhere.
/// The real locator comes with the platform layer.
/// </summary>
internal sealed class SmokeAria2Locator : IAria2Locator
{
    public string? FindAria2(string? configuredPath)
    {
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        if (OperatingSystem.IsWindows() && FindRepositoryRoot() is { } root)
        {
            var bundled = Path.Combine(root, "third_party", "aria2", "win-x64", "aria2c.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        var name = OperatingSystem.IsWindows() ? "aria2c.exe" : "aria2c";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Colibri.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}

/// <summary>Writes engine log lines to the console.</summary>
internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            var error = exception is null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})";
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] [{logLevel}] {formatter(state, exception)}{error}");
        }
    }
}
