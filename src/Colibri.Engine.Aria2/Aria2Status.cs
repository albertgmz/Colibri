using System.Globalization;
using System.Text.Json.Nodes;
using Colibri.Core.Engine;

namespace Colibri.Engine.Aria2;

/// <summary>
/// Turns aria2 status objects (from tellStatus, tellActive, tellWaiting and tellStopped) into
/// <see cref="EngineDownloadStatus"/>.
/// </summary>
internal static class Aria2Status
{
    /// <summary>The status fields Colibri reads; passed to aria2 so it sends nothing else.</summary>
    public static readonly string[] Keys =
    [
        "gid", "status", "totalLength", "completedLength", "downloadSpeed", "connections",
        "errorCode", "files", "bitfield", "numPieces", "pieceLength", "uploadSpeed", "uploadLength", "bittorrent",
    ];

    public static EngineDownloadStatus Parse(JsonObject status)
    {
        var state = ParseState(status["status"]?.ToString());

        string? errorMessage = null;
        if (state == EngineDownloadState.Error)
        {
            var code = status["errorCode"]?.ToString();
            // Native messages can echo request URLs and proxy credentials.
            errorMessage = int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var errorCode)
                ? $"aria2 error {errorCode}" : "aria2 download failed";
        }

        // Single-file downloads only: files[0] is the output file. aria2 returns "" before it knows the path,
        // and uses forward slashes on Windows; GetFullPath turns them into the platform's separators.
        var allFiles = (status["files"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var isTorrent = status["bittorrent"] is JsonObject;
        var selectedFiles = isTorrent ? allFiles.Where(file => file["selected"]?.ToString() == "true").ToArray() : allFiles;
        var total = ParseLong(status["totalLength"]);
        var completed = ParseLong(status["completedLength"]);
        if (isTorrent && selectedFiles.Length > 0 && selectedFiles.All(file => file["length"] is not null))
        {
            total = selectedFiles.Sum(file => ParseLong(file["length"]));
            completed = selectedFiles.Sum(file => Math.Min(ParseLong(file["length"]), ParseLong(file["completedLength"])));
        }
        var path = selectedFiles.FirstOrDefault()?["path"]?.ToString();
        var filePath = string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);

        // Note: a paused download re-added against a control file reports totalLength and
        // completedLength as 0 until it is resumed; the real numbers come back once aria2 reads its
        // .aria2 control file again.
        return new EngineDownloadStatus(
            Handle: status["gid"]?.ToString() ?? string.Empty,
            State: state,
            TotalBytes: total,
            CompletedBytes: completed,
            DownloadSpeed: ParseLong(status["downloadSpeed"]),
            Connections: (int)ParseLong(status["connections"]),
            ErrorMessage: errorMessage,
            FilePath: filePath,
            Bitfield: status["bitfield"]?.ToString() is { Length: > 0 } bitfield ? bitfield : null,
            NumPieces: status["numPieces"] is null ? null : (int)ParseLong(status["numPieces"]),
            PieceLength: status["pieceLength"] is null ? null : ParseLong(status["pieceLength"]),
            UploadSpeed: ParseLong(status["uploadSpeed"]),
            UploadedBytes: ParseLong(status["uploadLength"]),
            IsSeeding: isTorrent && state == EngineDownloadState.Active && total > 0 && completed >= total);
    }

    public static EngineDownloadState ParseState(string? status) => status switch
    {
        "active" => EngineDownloadState.Active,
        "waiting" => EngineDownloadState.Waiting,
        "paused" => EngineDownloadState.Paused,
        "complete" => EngineDownloadState.Complete,
        "error" => EngineDownloadState.Error,
        "removed" => EngineDownloadState.Removed,
        _ => throw new FormatException("Unknown aria2 download status."),
    };

    /// <summary>
    /// Reads an aria2 number. aria2 sends numbers as JSON strings ("33423360"); a missing or
    /// unreadable value counts as 0.
    /// </summary>
    public static long ParseLong(JsonNode? node) =>
        long.TryParse(node?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
