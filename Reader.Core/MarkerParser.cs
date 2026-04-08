using System.Text;
using Reader.Models;

namespace Reader.Core;

/// <summary>
/// Parses the raw bytes of a ReaderBridge marker string into a <see cref="ReaderSnapshot"/>.
/// </summary>
/// <remarks>
/// Preferred v2 format (13 pipe-delimited fields between markers):
/// ##READER_V2##|name|level|calling|hpPct|resourceKind|resourcePct|x|y|z|targetName|targetLevel|targetHpPct|targetRelation|##END_READER_V2##
///
/// Legacy v1 format (16 pipe-delimited fields between markers):
/// ##READER_DATA##|name|level|calling|guild|hp|hpMax|resourceKind|resource|resourceMax|x|y|z|targetName|targetLevel|targetHpPct|targetRelation|##END_READER##
/// </remarks>
public static class MarkerParser
{
    private const string V2StartMarker = "##READER_V2##|";
    private const string V2EndMarker = "|##END_READER_V2##";
    private const int V2ExpectedFieldCount = 13;

    private const string V1StartMarker = "##READER_DATA##|";
    private const string V1EndMarker = "|##END_READER##";
    private const int V1ExpectedFieldCount = 16;

    private static ReadOnlySpan<byte> V2StartBytes => "##READER_V2##|"u8;
    private static ReadOnlySpan<byte> V2EndBytes => "##END_READER_V2##"u8;
    private static ReadOnlySpan<byte> V1StartBytes => "##READER_DATA##|"u8;
    private static ReadOnlySpan<byte> V1EndBytes => "##END_READER##"u8;

    public static string PreferredStartMarker => V2StartMarker;
    public static string PreferredEndMarker => V2EndMarker;

    public static ReadOnlySpan<byte> PreferredStartBytes => V2StartBytes;
    public static ReadOnlySpan<byte> PreferredEndBytes => V2EndBytes;
    public static ReadOnlySpan<byte> LegacyStartBytes => V1StartBytes;
    public static ReadOnlySpan<byte> LegacyEndBytes => V1EndBytes;

    /// <summary>
    /// Finds and parses the marker string within a raw memory buffer.
    /// Prefers v2 if both formats are present.
    /// Returns null if no supported marker is found or the data is malformed.
    /// </summary>
    public static ReaderSnapshot? ParseFromBuffer(ReadOnlySpan<byte> buffer)
    {
        return ParseV2FromBuffer(buffer)
            ?? ParseV1FromBuffer(buffer);
    }

    private static ReaderSnapshot? ParseV2FromBuffer(ReadOnlySpan<byte> buffer)
    {
        int start = buffer.IndexOf(V2StartBytes);
        if (start < 0) return null;

        int dataStart = start + V2StartBytes.Length;
        ReadOnlySpan<byte> remainder = buffer[dataStart..];

        int end = remainder.IndexOf(V2EndBytes);
        if (end < 0) return null;

        return ParseV2Fields(remainder[..end]);
    }

    private static ReaderSnapshot? ParseV1FromBuffer(ReadOnlySpan<byte> buffer)
    {
        int start = buffer.IndexOf(V1StartBytes);
        if (start < 0) return null;

        int dataStart = start + V1StartBytes.Length;
        ReadOnlySpan<byte> remainder = buffer[dataStart..];

        int end = remainder.IndexOf(V1EndBytes);
        if (end < 0) return null;

        return ParseV1Fields(remainder[..end]);
    }

    /// <summary>
    /// Parses a v2 field block (content between markers, excluding the markers themselves).
    /// </summary>
    public static ReaderSnapshot? ParseV2Fields(ReadOnlySpan<byte> fieldBytes)
    {
        Span<Range> ranges = stackalloc Range[V2ExpectedFieldCount + 2];
        int count = SplitOnPipe(fieldBytes, ranges);

        if (count < V2ExpectedFieldCount) return null;

        string? name            = GetString(fieldBytes, ranges[0]);
        int?    level           = GetInt(fieldBytes, ranges[1]);
        string? calling         = GetString(fieldBytes, ranges[2]);
        int?    hpPercent       = GetInt(fieldBytes, ranges[3]);
        string? resourceKind    = GetString(fieldBytes, ranges[4]);
        int?    resourcePercent = GetInt(fieldBytes, ranges[5]);
        float?  x               = GetFloat(fieldBytes, ranges[6]);
        float?  y               = GetFloat(fieldBytes, ranges[7]);
        float?  z               = GetFloat(fieldBytes, ranges[8]);
        string? targetName      = GetString(fieldBytes, ranges[9]);
        int?    targetLevel     = GetInt(fieldBytes, ranges[10]);
        int?    targetHpPct     = GetInt(fieldBytes, ranges[11]);
        string? targetRel       = GetString(fieldBytes, ranges[12]);

        var identity = new PlayerIdentity(name, level, calling, Guild: null);
        var stats = new PlayerStats(
            Hp: null,
            HpMax: null,
            HpPercent: hpPercent,
            ResourceKind: resourceKind,
            Resource: null,
            ResourceMax: null,
            ResourcePercent: resourcePercent);
        var position = new PlayerPosition(x, y, z);

        TargetInfo? target = targetName is not null
            ? new TargetInfo(targetName, targetLevel, targetHpPct, targetRel)
            : null;

        return new ReaderSnapshot(ReaderPayloadVersion.V2, identity, stats, position, target, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Parses a v1 field block (content between markers, excluding the markers themselves).
    /// </summary>
    public static ReaderSnapshot? ParseV1Fields(ReadOnlySpan<byte> fieldBytes)
    {
        Span<Range> ranges = stackalloc Range[V1ExpectedFieldCount + 2];
        int count = SplitOnPipe(fieldBytes, ranges);

        if (count < V1ExpectedFieldCount) return null;

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

        int? hpPercent = ComputePercent(hp, hpMax);
        int? resourcePercent = ComputePercent(resource, resourceMax);

        var identity = new PlayerIdentity(name, level, calling, guild);
        var stats = new PlayerStats(
            Hp: hp,
            HpMax: hpMax,
            HpPercent: hpPercent,
            ResourceKind: resourceKind,
            Resource: resource,
            ResourceMax: resourceMax,
            ResourcePercent: resourcePercent);
        var position = new PlayerPosition(x, y, z);

        TargetInfo? target = targetName is not null
            ? new TargetInfo(targetName, targetLevel, targetHpPct, targetRel)
            : null;

        return new ReaderSnapshot(ReaderPayloadVersion.V1, identity, stats, position, target, DateTimeOffset.UtcNow);
    }

    private static int? ComputePercent(int? current, int? max)
    {
        if (current is null || max is null || max <= 0) return null;
        return (int)Math.Round((double)current.Value * 100d / max.Value, MidpointRounding.AwayFromZero);
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
