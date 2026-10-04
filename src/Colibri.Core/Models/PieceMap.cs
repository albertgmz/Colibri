namespace Colibri.Core.Models;

public static class PieceMap
{
    /// <summary>Null means missing or invalid telemetry. Padding bits never count as file pieces.</summary>
    public static int? CompletedCount(string? bitfield, int? pieces)
    {
        if (pieces is not > 0 || bitfield is null || bitfield.Length < (pieces.Value + 3L) / 4) return null;
        var completed = 0;
        for (var i = 0; i < pieces.Value; i++)
        {
            var hex = bitfield[i / 4];
            var value = hex is >= '0' and <= '9' ? hex - '0'
                : hex is >= 'a' and <= 'f' ? hex - 'a' + 10
                : hex is >= 'A' and <= 'F' ? hex - 'A' + 10 : -1;
            if (value < 0) return null;
            if ((value & (8 >> (i % 4))) != 0) completed++;
        }
        return completed;
    }
}
