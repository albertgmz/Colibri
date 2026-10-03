using System.Globalization;

namespace Colibri.NativeHost;

/// <summary>
/// A small daily log file in Colibri's logs folder (<c>native-host-yyyyMMdd.log</c>, kept 14 days). Never
/// writes to the console: stdout belongs to the browser. Logging problems are ignored, so the log can never
/// stop the host from answering. Callers must not pass cookies or full URLs (use <c>UrlPolicy.Redact</c>).
/// </summary>
internal sealed class HostLog
{
    private const int KeepDays = 14;

    private readonly string? _folder;
    private readonly object _gate = new();

    public HostLog(string? folder)
    {
        _folder = folder;
    }

    /// <summary>
    /// The logs folder of Colibri's data folder, the same place the app uses (see <c>AppPaths</c> in
    /// Colibri.Platform; the host does not reference that project, whose Windows build differs from this one).
    /// </summary>
    public static HostLog OpenDefault()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colibri", "logs");
            Directory.CreateDirectory(folder);
            try
            {
                DeleteOldFiles(folder);
            }
            catch (Exception)
            {
                // An old file in use; it goes next time.
            }

            return new HostLog(folder);
        }
        catch (Exception)
        {
            return new HostLog(null);
        }
    }

    public void Info(string message) => Write("INF", message);

    public void Warning(string message) => Write("WRN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        if (_folder is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{Environment.ProcessId}] {message}{Environment.NewLine}");
        try
        {
            // Several host processes can run at once (one per browser message); a short lock per line is enough.
            lock (_gate)
            {
                File.AppendAllText(Path.Combine(_folder, $"native-host-{now:yyyyMMdd}.log"), line);
            }
        }
        catch (IOException)
        {
            // Another host process is writing; this line is lost.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteOldFiles(string folder)
    {
        var limit = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(folder, "native-host-*.log"))
        {
            if (File.GetLastWriteTime(file) < limit)
            {
                File.Delete(file);
            }
        }
    }
}
