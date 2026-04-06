using System.Text;
using Reader.Core;

namespace Reader.Tests;

/// <summary>
/// Tests MarkerParser.ParseFromBuffer with buffers that simulate raw memory regions —
/// no live process required.
/// </summary>
public class MemoryScannerTests
{
    private static byte[] BuildBuffer(string prefix, string marker, string suffix)
    {
        byte[] p = new byte[prefix.Length];   // zero-filled junk
        byte[] m = Encoding.UTF8.GetBytes(marker);
        byte[] s = new byte[suffix.Length];
        return [.. p, .. m, .. s];
        }

    private const string ValidMarker =
        "##READER_DATA##|Player|65|Cleric|Guild|9000|10000|mana|5000|7000|100.00|200.00|0.00||||##END_READER##";

    [Fact]
    public void ParseFromBuffer_MarkerAtStart_Parses()
    {
        byte[] buf = Encoding.UTF8.GetBytes(ValidMarker);
        var snap = MarkerParser.ParseFromBuffer(buf);
        Assert.NotNull(snap);
        Assert.Equal("Player", snap.Player.Name);
    }

    [Fact]
    public void ParseFromBuffer_MarkerAfterJunk_Parses()
    {
        byte[] buf = BuildBuffer(new string('\0', 512), ValidMarker, new string('\0', 256));
        var snap = MarkerParser.ParseFromBuffer(buf);
        Assert.NotNull(snap);
        Assert.Equal("Player", snap.Player.Name);
        Assert.Equal(65, snap.Player.Level);
    }

    [Fact]
    public void ParseFromBuffer_MarkerNearEnd_Parses()
    {
        byte[] buf = BuildBuffer(new string('\0', 2048), ValidMarker, "");
        var snap = MarkerParser.ParseFromBuffer(buf);
        Assert.NotNull(snap);
        Assert.Equal("mana", snap.Stats.ResourceKind);
    }

    [Fact]
    public void ParseFromBuffer_NoMarkerInBuffer_ReturnsNull()
    {
        byte[] buf = new byte[1024]; // all zeros
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_PartialMarkerTruncated_ReturnsNull()
    {
        // Start marker present but no end marker
        byte[] buf = Encoding.UTF8.GetBytes("##READER_DATA##|Player|65|Cleric|Guild|9000|10000");
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_MultipleMarkersInBuffer_ParsesFirst()
    {
        // Simulate two marker strings in the same buffer (e.g. Lua GC kept old copy)
        string old = "##READER_DATA##|OldPlayer|60|Warrior||1000|5000|energy|80|100|0.00|0.00|0.00||||##END_READER##";
        string current = ValidMarker;
        byte[] buf = [.. Encoding.UTF8.GetBytes(old), .. new byte[64], .. Encoding.UTF8.GetBytes(current)];
        var snap = MarkerParser.ParseFromBuffer(buf);
        Assert.NotNull(snap);
        // Should parse the first (old) marker
        Assert.Equal("OldPlayer", snap.Player.Name);
    }
}
