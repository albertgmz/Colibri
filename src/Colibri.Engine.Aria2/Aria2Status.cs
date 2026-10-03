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
        "errorCode", "errorMessage", "files", "bitfield", "numPieces", "pieceLength",
    ];

    public static EngineDownloadStatus Parse(JsonObject status)
    {
        var state = ParseState(status["status"]?.ToString());

        string? errorMessage = null;
        if (state == EngineDownloadState.Error)
        {
            var message = status["errorMessage"]?.ToString();
            var code = status["errorCode"]?.ToString();
            errorMessage = string.IsNullOrEmpty(message) ? $"aria2 error {code}" : $"{message} (aria2 error {code})";
        }

        // Single-file downloads only: files[0] is the output file. aria2 returns "" before it knows the path,
        // and uses forward slashes on Windows; GetFullPath turns them into the platform's separators.
        var path = (status["files"] as JsonArray)?.FirstOrDefault()?["path"]?.ToString();
        var filePath = string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);

        // Note: a paused download restored from the session file reports totalLength and
        // completedLength as 0 until it is resumed; the real numbers come back once aria2 reads its
        // .aria2 control file again.
        return new EngineDownloadStatus(
            Handle: status["gid"]?.ToString() ?? string.Empty,
            State: state,
            TotalBytes: ParseLong(status["totalLength"]),
            CompletedBytes: ParseLong(status["completedLength"]),
            DownloadSpeed: ParseLong(status["downloadSpeed"]),
            Connections: (int)ParseLong(status["connections"]),
            ErrorMessage: errorMessage,
            FilePath: filePath,
            Bitfield: status["bitfield"]?.ToString() is { Length: > 0 } bitfield ? bitfield : null,
            NumPieces: status["numPieces"] is null ? null : (int)ParseLong(status["numPieces"]),
            PieceLength: status["pieceLength"] is null ? null : ParseLong(status["pieceLength"]));
    }

    public static EngineDownloadState ParseState(string? status) => status switch
    {
        "active" => EngineDownloadState.Active,
        "waiting" => EngineDownloadState.Waiting,
        "paused" => EngineDownloadState.Paused,
        "complete" => EngineDownloadState.Complete,
        "error" => EngineDownloadState.Error,
        "removed" => EngineDownloadState.Removed,
        _ => throw new FormatException($"Unknown aria2 download status '{status}'."),
    };

    /// <summary>
    /// Reads an aria2 number. aria2 sends numbers as JSON strings ("33423360"); a missing or
    /// unreadable value counts as 0.
    /// </summary>
    public static long ParseLong(JsonNode? node) =>
        long.TryParse(node?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
