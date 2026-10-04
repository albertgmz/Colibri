namespace Colibri.Core.Engine;

/// <summary>
/// Snapshot of one download as reported by the engine.
/// </summary>
/// <param name="Handle">Engine handle (for aria2: the GID).</param>
/// <param name="State">Engine-side state.</param>
/// <param name="TotalBytes">Total size in bytes; 0 while unknown.</param>
/// <param name="CompletedBytes">Bytes downloaded so far.</param>
/// <param name="DownloadSpeed">Bytes per second.</param>
/// <param name="Connections">Open connections.</param>
/// <param name="ErrorMessage">Error text when <paramref name="State"/> is Error.</param>
/// <param name="FilePath">Full path of the output file, when known.</param>
/// <param name="Bitfield">Hex string of downloaded pieces (highest bit of the first byte = piece 0).</param>
/// <param name="NumPieces">Number of pieces.</param>
/// <param name="PieceLength">Piece size in bytes.</param>
public sealed record EngineDownloadStatus(
    string Handle,
    EngineDownloadState State,
    long TotalBytes,
    long CompletedBytes,
    long DownloadSpeed,
    int Connections,
    string? ErrorMessage = null,
    string? FilePath = null,
    string? Bitfield = null,
    int? NumPieces = null,
    long? PieceLength = null,
    long UploadSpeed = 0,
    long UploadedBytes = 0,
    bool IsSeeding = false);
