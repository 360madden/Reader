using Reader.Core;
using Reader.Models;

namespace Reader.Tests;

/// <summary>
/// Buffer-level tests for the v3 parser via MarkerParser. The full
/// MemoryScanner requires a live process and is exercised by the
/// in-game smoke test.
/// </summary>
public class MemoryScannerTests
{
    private static byte[] BuildV3()
    {
        var enc = new V3Encoder();
        return enc.Build(
            seq: 1,
            frameTimeMs: 0,
            flags: ReaderFlags.None,
            'A',
            new ReaderSnapshot(
                ReaderPayloadVersion.V3,
                new PlayerIdentity("Player", 65, "Cleric", "Guild"),
                new PlayerStats(9000, 10000, 90, "mana", 5000, 7000, 71),
                new PlayerPosition(100, 200, 0),
                Target: null,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseFromBuffer_V3AtBufferStart_Parses()
    {
        var snap = MarkerParser.ParseFromBuffer(BuildV3());
        Assert.NotNull(snap);
        Assert.Equal("Player", snap.Player.Name);
        Assert.Equal(ReaderPayloadVersion.V3, snap.PayloadVersion);
    }

    [Fact]
    public void ParseFromBuffer_V3WithLeadingPadding_RequiresExactStart()
    {
        // The parser expects the magic at offset 0 of the slice it's given.
        // The MemoryScanner is responsible for finding the magic in a region
        // and slicing accordingly.
        byte[] withPad = new byte[V3Layout.TotalLen + 64];
        BuildV3().CopyTo(withPad, 64);
        Assert.Null(MarkerParser.ParseFromBuffer(withPad));

        // Slice at the right offset → succeeds
        var snap = MarkerParser.ParseFromBuffer(withPad.AsSpan(64, V3Layout.TotalLen));
        Assert.NotNull(snap);
    }

    [Fact]
    public void ParseFromBuffer_NoMarkerInBuffer_ReturnsNull()
    {
        Assert.Null(MarkerParser.ParseFromBuffer(new byte[V3Layout.TotalLen]));
    }

    [Fact]
    public void ParseFromBuffer_V3WithoutTarget_HasNullTarget()
    {
        var snap = MarkerParser.ParseFromBuffer(BuildV3());
        Assert.NotNull(snap);
        Assert.Null(snap.Target);
    }
}
