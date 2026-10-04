namespace Colibri.Core.Platform;

/// <summary>
/// The user chose <paramref name="Action"/> on the notification for download <paramref name="DownloadId"/>.
/// </summary>
public sealed record NotificationActionInvoked(Guid DownloadId, NotificationAction Action);
