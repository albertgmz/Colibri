namespace Colibri.Core.Models;

/// <summary>
/// A download as stored in the database. The database row is the source of truth;
/// <see cref="EngineHandle"/> is only a pointer into the engine that runs it.
/// </summary>
public sealed class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The URL the user asked for.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The URL after redirects, when known.</summary>
    public string? FinalUrl { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string SaveFolder { get; set; } = string.Empty;

    public DownloadCategory Category { get; set; } = DownloadCategory.Other;

    public DownloadState State { get; set; } = DownloadState.Queued;

    /// <summary>Total size in bytes; null while unknown.</summary>
    public long? TotalBytes { get; set; }

    public long CompletedBytes { get; set; }

    /// <summary>Current speed in bytes per second.</summary>
    public long DownloadSpeed { get; set; }

    public int Connections { get; set; }

    /// <summary>Id of the <see cref="Engine.IDownloadEngine"/> that runs this download.</summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>The engine's own handle for this download (for aria2: the GID).</summary>
    public string? EngineHandle { get; set; }

    public string? Referrer { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Extra request headers (case-insensitive names).</summary>
    public Dictionary<string, string> Headers { get; set; } = HttpHeaders.Create();

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }
}
