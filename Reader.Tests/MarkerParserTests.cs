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

    // ─── v4 phase-2 section round-trips ────────────────────────────────

    [Fact]
    public void V3Encoder_Cooldowns_RoundTrip()
    {
        var enc = new V3Encoder();
        var cooldowns = new List<CooldownInfo>
        {
            new(AbilityId: 4001, Name: "Fireball",    RemainMs: 0,    DurationMs: 1500, ResourceCost: 30, ResourceKind: "mana"),
            new(AbilityId: 4002, Name: "Wild Growth", RemainMs: 7800, DurationMs: 15000, ResourceCost: 0,  ResourceKind: null),
        };
        var snap = SampleSnapshot() with { Cooldowns = cooldowns };
        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        var parsed = MarkerParser.ParseFromBuffer(buf);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Cooldowns);
        Assert.Equal(2, parsed.Cooldowns.Count);
        Assert.Equal(4001, parsed.Cooldowns[0].AbilityId);
        Assert.Equal("Fireball", parsed.Cooldowns[0].Name);
        Assert.Equal(0, parsed.Cooldowns[0].RemainMs);
        Assert.Equal(1500, parsed.Cooldowns[0].DurationMs);
        Assert.Equal(30, parsed.Cooldowns[0].ResourceCost);
        Assert.Equal("mana", parsed.Cooldowns[0].ResourceKind);
        Assert.Equal(7800, parsed.Cooldowns[1].RemainMs);
        Assert.Null(parsed.Cooldowns[1].ResourceKind);
    }

    [Fact]
    public void V3Encoder_Attributes_RoundTrip()
    {
        var enc = new V3Encoder();
        var attrs = new CharacterStats(
            Strength: 120, Dexterity: 85, Intelligence: 540, Wisdom: 310, Endurance: 420,
            Armor: 3200, DeflectChance: 150, DodgeChance: 80, ParryChance: 10,
            ResistAir: 0, ResistDeath: 50, ResistEarth: 25, ResistFire: 200, ResistLife: 30, ResistWater: 10,
            CritHit: 1250, Hit: 140, AttackPower: 400, SpellPower: 980,
            Physical: 0, Spell: 220);
        var snap = SampleSnapshot() with { Attributes = attrs };
        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        var parsed = MarkerParser.ParseFromBuffer(buf);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Attributes);
        Assert.Equal(120, parsed.Attributes.Strength);
        Assert.Equal(540, parsed.Attributes.Intelligence);
        Assert.Equal(3200, parsed.Attributes.Armor);
        Assert.Equal(200, parsed.Attributes.ResistFire);
        Assert.Equal(1250, parsed.Attributes.CritHit);
        Assert.Equal(980, parsed.Attributes.SpellPower);
        Assert.Equal(220, parsed.Attributes.Spell);
    }

    [Fact]
    public void V3Encoder_Equipment_RoundTrip()
    {
        var enc = new V3Encoder();
        var eq = new List<EquipmentItem>
        {
            new(Slot: "Chest",    ItemId: "I-10045",  Name: "Plate of the Ascended",      Rarity: "epic",   ItemLevel: 425, Augments: 2),
            new(Slot: "MainHand", ItemId: "I-99999",  Name: "Sword of |;= Edge Cases",    Rarity: "relic",  ItemLevel: 450, Augments: 3),
            new(Slot: "Trinket1", ItemId: null,       Name: "Unidentified Trinket",       Rarity: null,     ItemLevel: null, Augments: null),
        };
        var snap = SampleSnapshot() with { Equipment = eq };
        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        var parsed = MarkerParser.ParseFromBuffer(buf);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Equipment);
        Assert.Equal(3, parsed.Equipment.Count);
        Assert.Equal("Chest", parsed.Equipment[0].Slot);
        Assert.Equal("I-10045", parsed.Equipment[0].ItemId);
        Assert.Equal("Plate of the Ascended", parsed.Equipment[0].Name);
        Assert.Equal("epic", parsed.Equipment[0].Rarity);
        Assert.Equal(425, parsed.Equipment[0].ItemLevel);
        Assert.Equal(2, parsed.Equipment[0].Augments);
        // Embedded delimiters in item name survive length-prefix encoding.
        Assert.Equal("Sword of |;= Edge Cases", parsed.Equipment[1].Name);
        // Null-ish item survives round-trip as null
        Assert.Equal("Trinket1", parsed.Equipment[2].Slot);
        Assert.Null(parsed.Equipment[2].ItemId);
        Assert.Null(parsed.Equipment[2].Rarity);
    }

    [Fact]
    public void V3Encoder_Currency_RoundTrip()
    {
        var enc = new V3Encoder();
        var cur = new List<CurrencyEntry>
        {
            new(Id: "PLAT", Name: "Platinum",      Amount: 12345678L, Max: null),
            new(Id: "PLNR", Name: "Planarite",     Amount: 50000L,    Max: 1_000_000L),
            new(Id: "VS",   Name: "Void Stones",   Amount: 2500L,     Max: 3000L),
        };
        var snap = SampleSnapshot() with { Currencies = cur };
        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        var parsed = MarkerParser.ParseFromBuffer(buf);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Currencies);
        Assert.Equal(3, parsed.Currencies.Count);
        Assert.Equal("PLAT", parsed.Currencies[0].Id);
        Assert.Equal("Platinum", parsed.Currencies[0].Name);
        Assert.Equal(12345678L, parsed.Currencies[0].Amount);
        Assert.Null(parsed.Currencies[0].Max);
        Assert.Equal(1_000_000L, parsed.Currencies[1].Max);
    }

    [Fact]
    public void V3Encoder_Group_RoundTrip()
    {
        var enc = new V3Encoder();
        var grp = new List<GroupMember>
        {
            new(UnitId: "p101.1", Name: "Brethor",  Level: 70, Calling: "Warrior", Role: "Tank",  HpPercent: 100, ResourcePercent: 80, IsOnline: true,  IsDead: false, ZoneName: "Mathosia"),
            new(UnitId: "p101.2", Name: "Selyndra", Level: 70, Calling: "Cleric",  Role: "Heal",  HpPercent: 95,  ResourcePercent: 60, IsOnline: true,  IsDead: false, ZoneName: "Mathosia"),
            new(UnitId: "p101.3", Name: "Offline",  Level: 65, Calling: "Mage",    Role: "DPS",   HpPercent: 0,   ResourcePercent: 0,  IsOnline: false, IsDead: false, ZoneName: null),
        };
        var snap = SampleSnapshot() with { Group = grp };
        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        var parsed = MarkerParser.ParseFromBuffer(buf);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Group);
        Assert.Equal(3, parsed.Group.Count);
        Assert.Equal("Brethor", parsed.Group[0].Name);
        Assert.Equal("Tank", parsed.Group[0].Role);
        Assert.True(parsed.Group[0].IsOnline);
        Assert.False(parsed.Group[2].IsOnline);
        Assert.Null(parsed.Group[2].ZoneName);
    }

    [Fact]
    public void V3Encoder_AllPhase2Sections_FitWithin8KBody()
    {
        // Stress test: every phase-2 section populated with realistic volumes.
        var enc = new V3Encoder();
        var cooldowns = Enumerable.Range(0, 30).Select(i =>
            new CooldownInfo(1000 + i, $"Ability{i}", i * 100, 30000, 50, "mana")).ToList();
        var equipment = Enumerable.Range(0, 17).Select(i =>
            new EquipmentItem($"Slot{i}", $"I-{i:D5}", $"Item {i} of Testing", "epic", 400 + i, 2)).ToList();
        var currencies = Enumerable.Range(0, 10).Select(i =>
            new CurrencyEntry($"CUR{i}", $"Currency {i}", 1000L * i, null)).ToList();
        var group = Enumerable.Range(0, 5).Select(i =>
            new GroupMember($"p1.{i}", $"Member{i}", 70, "Warrior", "DPS", 100, 80, true, false, "Zone")).ToList();
        var attrs = new CharacterStats(100, 100, 500, 300, 400,
            3000, 150, 80, 10, 0, 50, 25, 200, 30, 10, 1250, 140, 400, 980, 0, 220);

        var snap = SampleSnapshot() with
        {
            Cooldowns = cooldowns,
            Attributes = attrs,
            Equipment = equipment,
            Currencies = currencies,
            Group = group,
        };

        byte[] buf = enc.Build(seq: 1, frameTimeMs: 0, flags: ReaderFlags.None, 'A', snap);
        Assert.Equal(V3Layout.TotalLen, buf.Length);

        var parsed = MarkerParser.ParseFromBuffer(buf);
        Assert.NotNull(parsed);
        Assert.Equal(30, parsed.Cooldowns!.Count);
        Assert.Equal(17, parsed.Equipment!.Count);
        Assert.Equal(10, parsed.Currencies!.Count);
        Assert.Equal(5, parsed.Group!.Count);
        Assert.NotNull(parsed.Attributes);
    }
}
