using Reader.Core;
using Reader.Models;

namespace Reader.Tests;

public class MarkerParserTests
{
    private static ReaderSnapshot SampleSnapshot() => new(
        ReaderPayloadVersion.V4,
        new PlayerIdentity("Arthok", 70, "Mage", "Pipes|And;Equals=InGuild"),
        new PlayerStats(12500, 15000, 83, "mana", 8900, 10000, 89),
        new PlayerPosition(1234.56f, 789.01f, -45.23f),
        new TargetInfo("Dragnoth", 72, 55, "hostile"),
        DateTimeOffset.UtcNow,
        Seq: 0xDEADBEEFCAFE,
        FrameTimeMs: 0x123456,
        Flags: ReaderFlags.HasTarget | ReaderFlags.InCombat,
        Combat: new CombatStats(2150.5, 1980.2, 0, 0, 350.1, 280.4),
        Zone: new ZoneInfo(38, "Mathosia"));

    private static byte[] BuildValid(ulong seq = 0xDEADBEEFCAFE, char active = 'A')
    {
        var enc = new V3Encoder();
        return enc.Build(seq, frameTimeMs: 0x123456, flags: ReaderFlags.HasTarget | ReaderFlags.InCombat, active, SampleSnapshot());
    }

    [Fact]
    public void ParseFromBuffer_ValidV4_RoundTripsAllSections()
    {
        var snap = MarkerParser.ParseFromBuffer(BuildValid());

        Assert.NotNull(snap);
        Assert.Equal(ReaderPayloadVersion.V4, snap.PayloadVersion);
        Assert.Equal(0xDEADBEEFCAFEul, snap.Seq);
        Assert.Equal(0x123456L, snap.FrameTimeMs);
        Assert.Equal(ReaderFlags.HasTarget | ReaderFlags.InCombat, snap.Flags);

        Assert.Equal("Arthok", snap.Player.Name);
        Assert.Equal(70, snap.Player.Level);
        Assert.Equal("Mage", snap.Player.Calling);
        Assert.Equal("Pipes|And;Equals=InGuild", snap.Player.Guild);

        Assert.Equal(12500, snap.Stats.Hp);
        Assert.Equal(15000, snap.Stats.HpMax);
        Assert.Equal(83, snap.Stats.HpPercent);
        Assert.Equal("mana", snap.Stats.ResourceKind);
        Assert.Equal(8900, snap.Stats.Resource);
        Assert.Equal(10000, snap.Stats.ResourceMax);
        Assert.Equal(89, snap.Stats.ResourcePercent);

        Assert.Equal(1234.56f, snap.Position.X!.Value, precision: 2);
        Assert.Equal(789.01f, snap.Position.Y!.Value, precision: 2);
        Assert.Equal(-45.23f, snap.Position.Z!.Value, precision: 2);

        Assert.NotNull(snap.Target);
        Assert.Equal("Dragnoth", snap.Target.Name);
        Assert.Equal(72, snap.Target.Level);
        Assert.Equal(55, snap.Target.HpPercent);
        Assert.Equal("hostile", snap.Target.Relation);

        Assert.NotNull(snap.Zone);
        Assert.Equal(38, snap.Zone.Id);
        Assert.Equal("Mathosia", snap.Zone.Name);

        Assert.NotNull(snap.Combat);
        Assert.Equal(2150.5, snap.Combat.Dps1s, precision: 2);
    }

    [Fact]
    public void ParseFromBuffer_BothActiveSlots_ParseEquivalently()
    {
        var snapA = MarkerParser.ParseFromBuffer(BuildValid(active: 'A'));
        var snapB = MarkerParser.ParseFromBuffer(BuildValid(active: 'B'));

        Assert.NotNull(snapA);
        Assert.NotNull(snapB);
        Assert.Equal(snapA.Player.Name, snapB.Player.Name);
        Assert.Equal(snapA.Seq, snapB.Seq);
    }

    [Fact]
    public void ParseFromBuffer_LengthPrefixedString_AllowsEmbeddedDelimiters()
    {
        var snap = MarkerParser.ParseFromBuffer(BuildValid());
        // The sample guild contains '|', ';', and '=' which would break naive splitting.
        Assert.Equal("Pipes|And;Equals=InGuild", snap!.Player.Guild);
    }

    [Fact]
    public void ParseFromBuffer_NoMagic_ReturnsNull()
    {
        byte[] buf = new byte[V3Layout.TotalLen];
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_BufferTooSmall_ReturnsNull()
    {
        Assert.Null(MarkerParser.ParseFromBuffer(new byte[V3Layout.TotalLen - 1]));
    }

    [Fact]
    public void ParseFromBuffer_CrcMismatch_ReturnsNull()
    {
        byte[] buf = BuildValid();
        // Flip a byte inside slot A's body — CRC must fail.
        buf[V3Layout.SlotAOff + V3Layout.BodyOff + 5] ^= 0xFF;
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_TornRead_SeqMismatch_ReturnsNull()
    {
        byte[] buf = BuildValid();
        // Corrupt the control-block seq so it no longer matches the slot's seq.
        buf[V3Layout.ControlOff + V3Layout.CtrlSeqOff] = (byte)'F';
        buf[V3Layout.ControlOff + V3Layout.CtrlSeqOff + 1] = (byte)'F';
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_BadVersionByte_ReturnsNull()
    {
        byte[] buf = BuildValid();
        buf[V3Layout.SlotAOff + V3Layout.HdrVerOff + 1] = (byte)'9';
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_BadActiveByte_ReturnsNull()
    {
        byte[] buf = BuildValid();
        buf[V3Layout.ControlOff + V3Layout.CtrlActiveOff] = (byte)'X';
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void ParseFromBuffer_SentinelClobbered_ReturnsNull()
    {
        byte[] buf = BuildValid();
        buf[V3Layout.SlotAOff + V3Layout.SlotEndOff] = 0;
        Assert.Null(MarkerParser.ParseFromBuffer(buf));
    }

    [Fact]
    public void V3Encoder_TotalLength_AlwaysFixed()
    {
        var enc = new V3Encoder();
        var bigBuffs = Enumerable.Range(0, 50)
            .Select(i => new BuffInfo(i, $"Buff{i}", 1, 30000, true))
            .ToList();
        var snap = SampleSnapshot() with { PlayerBuffs = bigBuffs };
        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        Assert.Equal(V3Layout.TotalLen, buf.Length);
    }

    [Fact]
    public void V3Encoder_BuffList_RoundTrips()
    {
        var enc = new V3Encoder();
        var buffs = new List<BuffInfo>
        {
            new(101, "Quickness", 1, 12000, true),
            new(202, "Burning", 3, 8000, false),
        };
        var snap = SampleSnapshot() with { PlayerBuffs = buffs };
        byte[] buf = enc.Build(seq: 7, frameTimeMs: 99, flags: ReaderFlags.None, 'A', snap);
        var parsed = MarkerParser.ParseFromBuffer(buf);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.PlayerBuffs);
        Assert.Equal(2, parsed.PlayerBuffs.Count);
        Assert.Equal(101, parsed.PlayerBuffs[0].Id);
        Assert.Equal("Quickness", parsed.PlayerBuffs[0].Name);
        Assert.True(parsed.PlayerBuffs[0].CasterIsSelf);
        Assert.Equal(202, parsed.PlayerBuffs[1].Id);
        Assert.Equal(3, parsed.PlayerBuffs[1].Stacks);
        Assert.False(parsed.PlayerBuffs[1].CasterIsSelf);
    }
}
