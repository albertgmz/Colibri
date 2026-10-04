namespace Colibri.Platform.Paths;

/// <summary>macOS: ~/Downloads (Finder shows a translated name, but the folder on disk is always "Downloads").</summary>
internal sealed class MacDownloadsFolderLocator : IDownloadsFolderLocator
{
    public string GetDownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
