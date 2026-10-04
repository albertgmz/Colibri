using Colibri.Core.Platform;

namespace Colibri.Platform.Aria2;

/// <summary>
/// Linux and macOS: the configured aria2c if it exists, otherwise "aria2c" in the PATH folders, then in
/// the extra folders.
/// </summary>
internal sealed class UnixAria2Locator : IAria2Locator
{
    private readonly Func<string?> _pathVariable;
    private readonly IReadOnlyList<string> _extraFolders;

    public UnixAria2Locator(IReadOnlyList<string> extraFolders)
        : this(() => Environment.GetEnvironmentVariable("PATH"), extraFolders)
    {
    }

    internal UnixAria2Locator(Func<string?> pathVariable, IReadOnlyList<string> extraFolders)
    {
        _pathVariable = pathVariable;
        _extraFolders = extraFolders;
    }

    public string? FindAria2(string? configuredPath)
    {
        // Only a full path: a relative one would be looked up in the current directory.
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathFullyQualified(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var folders = (_pathVariable() ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

            // A relative PATH entry (such as ".") means the current directory; a program dropped there
            // must never be started as aria2.
            .Where(Path.IsPathFullyQualified)
            .Concat(_extraFolders);

        return folders
            .Select(folder => Path.Combine(folder, "aria2c"))
            .FirstOrDefault(File.Exists);
    }
}
