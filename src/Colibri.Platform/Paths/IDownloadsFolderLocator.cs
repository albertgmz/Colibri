namespace Colibri.Platform.Paths;

/// <summary>Finds the user's Downloads folder; one implementation per OS.</summary>
internal interface IDownloadsFolderLocator
{
    string GetDownloadsFolder();
}
