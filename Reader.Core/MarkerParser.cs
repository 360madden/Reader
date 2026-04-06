using System.Text;
using Reader.Models;

namespace Reader.Core;

/// <summary>
/// Parses the raw bytes of a ReaderBridge marker string into a <see cref="ReaderSnapshot"/>.
/// </summary>
/// <remarks>
/// Expected format (16 pipe-delimited fields between markers):
/// ##READER_DATA##|name|level|calling|guild|hp|hpMax|resourceKind|resource|resourceMax|x|y|z|targetName|targetLevel|targetHpPct|targetRelation|##END_READER##
/// </remarks>
public static class MarkerParser
{
    private const string StartMarker = "##READER_DATA##|";
    private const string EndMarker = "|##END_READER##";
    private const int ExpectedFieldCount = 16;

    private static ReadOnlySpan<byte> StartBytes => "##READER_DATA##|"u8;
    private static ReadOnlySpan<byte> EndBytes   => "##END_READER##"u8;

    /// <summary>
    /// Finds and parses the marker string within a raw memory buffer.
    /// Returns null if the marker is not found or the data is malformed.
    /// </summary>
    public static ReaderSnapshot? ParseFromBuffer(ReadOnlySpan<byte> buffer)
    {
        int start = buffer.IndexOf(StartBytes);
        if (start < 0) return null;

        int dataStart = start + StartBytes.Length;
        ReadOnlySpan<byte> remainder = buffer[dataStart..];

        int end = remainder.IndexOf(EndBytes);
        if (end < 0) return null;

        return ParseFields(remainder[..end]);
    }

    /// <summary>
    /// Parses the pipe-delimited field bytes (the content between the markers, excluding the markers themselves).
    /// </summary>
    public static ReaderSnapshot? ParseFields(ReadOnlySpan<byte> fieldBytes)
    {
        // Split on '|' (0x7C) manually -- SplitAny is chars-only
        Span<Range> ranges = stackalloc Range[ExpectedFieldCount + 2];
        int count = SplitOnPipe(fieldBytes, ranges);

        if (count < ExpectedFieldCount) return null;

        string? name         = GetString(fieldBytes, ranges[0]);
        int?    level        = GetInt(fieldBytes, ranges[1]);
        string? calling      = GetString(fieldBytes, ranges[2]);
        string? guild        = GetString(fieldBytes, ranges[3]);
        int?    hp           = GetInt(fieldBytes, ranges[4]);
        int?    hpMax        = GetInt(fieldBytes, ranges[5]);
        string? resourceKind = GetString(fieldBytes, ranges[6]);
        int?    resource     = GetInt(fieldBytes, ranges[7]);
        int?    resourceMax  = GetInt(fieldBytes, ranges[8]);
        float?  x            = GetFloat(fieldBytes, ranges[9]);
        float?  y            = GetFloat(fieldBytes, ranges[10]);
        float?  z            = GetFloat(fieldBytes, ranges[11]);
        string? targetName   = GetString(fieldBytes, ranges[12]);
        int?    targetLevel  = GetInt(fieldBytes, ranges[13]);
        int?    targetHpPct  = GetInt(fieldBytes, ranges[14]);
        string? targetRel    = GetString(fieldBytes, ranges[15]);

        var identity = new PlayerIdentity(name, level, calling, guild);
        var stats    = new PlayerStats(hp, hpMax, resourceKind, resource, resourceMax);
        var position = new PlayerPosition(x, y, z);

        TargetInfo? target = targetName is not null
            ? new TargetInfo(targetName, targetLevel, targetHpPct, targetRel)
            : null;

        return new ReaderSnapshot(identity, stats, position, target, DateTimeOffset.UtcNow);
    }

    private static string? GetString(ReadOnlySpan<byte> data, Range range)
    {
        ReadOnlySpan<byte> slice = data[range];
        if (slice.IsEmpty) return null;
        return Encoding.UTF8.GetString(slice);
    }

    private static int? GetInt(ReadOnlySpan<byte> data, Range range)
    {
        ReadOnlySpan<byte> slice = data[range];
        if (slice.IsEmpty) return null;
        string s = Encoding.ASCII.GetString(slice);
        return int.TryParse(s, out int v) ? v : null;
    }

    private static float? GetFloat(ReadOnlySpan<byte> data, Range range)
    {
        ReadOnlySpan<byte> slice = data[range];
        if (slice.IsEmpty) return null;
        string s = Encoding.ASCII.GetString(slice);
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;
    }

    private static int SplitOnPipe(ReadOnlySpan<byte> data, Span<Range> ranges)
    {
        int count = 0;
        int start = 0;
        for (int i = 0; i < data.Length && count < ranges.Length - 1; i++)
        {
            if (data[i] == (byte)'|')
            {
                ranges[count++] = new Range(start, i);
                start = i + 1;
            }
        }
        if (count < ranges.Length)
            ranges[count++] = new Range(start, data.Length);
        return count;
    }
}
