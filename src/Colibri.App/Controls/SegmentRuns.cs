namespace Colibri.App.Controls;

/// <summary>A stretch of consecutive downloaded pieces.</summary>
/// <param name="Start">Index of the first piece.</param>
/// <param name="Count">Number of pieces.</param>
public readonly record struct PieceRun(int Start, int Count);

/// <summary>Turns an engine's piece bitfield into runs of downloaded pieces.</summary>
public static class SegmentRuns
{
    /// <summary>
    /// Reads <paramref name="bitfield"/> (hex; the highest bit of the first byte is piece 0) and returns
    /// the runs of downloaded pieces among the first <paramref name="pieceCount"/>, in order. Adjacent
    /// pieces are merged, so a file with thousands of pieces gives only a handful of runs. Bits past the
    /// end of the string count as not downloaded. Returns null when there is no bitfield or it is not hex,
    /// so the caller can fall back to plain progress.
    /// </summary>
    public static IReadOnlyList<PieceRun>? FromBitfield(string? bitfield, int pieceCount)
    {
        var runs = new List<PieceRun>();
        if (string.IsNullOrEmpty(bitfield) || pieceCount <= 0)
        {
            return null;
        }

        var available = Math.Min(pieceCount, bitfield.Length * 4);
        var runStart = -1;
        for (var piece = 0; piece < available; piece++)
        {
            var nibble = HexValue(bitfield[piece / 4]);
            if (nibble < 0)
            {
                return null;
            }

            var done = (nibble & (0b1000 >> (piece % 4))) != 0;
            if (done && runStart < 0)
            {
                runStart = piece;
            }
            else if (!done && runStart >= 0)
            {
                runs.Add(new PieceRun(runStart, piece - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            runs.Add(new PieceRun(runStart, available - runStart));
        }

        return runs;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
