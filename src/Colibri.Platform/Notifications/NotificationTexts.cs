namespace Colibri.Platform.Notifications;

/// <summary>
/// User-facing texts of the download notifications. Colibri.Platform has no resources of its own: the app
/// passes translated texts to <see cref="PlatformServiceCollectionExtensions.AddColibriPlatform"/>, and the
/// English defaults below are used when it passes none.
/// </summary>
public sealed record NotificationTexts
{
    public string DownloadCompleteTitle { get; init; } = "Download complete";

    public string DownloadFailedTitle { get; init; } = "Download failed";

    public string Open { get; init; } = "Open";

    public string ShowInFolder { get; init; } = "Show in folder";

    public string Retry { get; init; } = "Retry";
}
