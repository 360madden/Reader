using System.Text;
using Reader.Models;

namespace Reader.Core;

/// <summary>
/// Builds v3 wire-format buffers. The C# implementation is the executable
/// spec; the Lua addon side mirrors the same byte layout exactly.
///
/// Primarily used by tests and tooling. Hot-path scanning never goes through
/// here — it parses bytes directly via <see cref="MarkerParser"/>.
/// </summary>
public sealed class V3Encoder
{
    private readonly StringBuilder _bodyBuilder = new(capacity: V3Layout.SlotBodyMax);

    /// <summary>
    /// Build a complete 16584-byte v4 buffer with a single populated slot.
    /// The other slot is filled with zeros (and a stale-seq header so the
    /// scanner ignores it).
    /// </summary>
    public byte[] Build(
        ulong seq,
        long frameTimeMs,
        ReaderFlags flags,
        char activeSlot,
        ReaderSnapshot snapshot)
    {
        var buf = new byte[V3Layout.TotalLen];

        // 1. MAGIC
        V3Layout.Magic.CopyTo(buf.AsSpan(0, V3Layout.MagicLen));

        // 2. CONTROL block
        var ctrl = buf.AsSpan(V3Layout.ControlOff, V3Layout.ControlLen);
        ctrl[V3Layout.CtrlActiveOff] = (byte)activeSlot;
        ctrl[1] = (byte)'|';
        V3Layout.WriteHex(ctrl.Slice(V3Layout.CtrlSeqOff, 16), seq, 16);
        ctrl[18] = (byte)'|';
        V3Layout.WriteHex(ctrl.Slice(V3Layout.CtrlTOff, 12), (ulong)frameTimeMs, 12);
        ctrl[31] = (byte)'\n';

        // 3. Build BODY
        _bodyBuilder.Clear();
        uint sec = BuildBody(_bodyBuilder, snapshot);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(_bodyBuilder.ToString());
        if (bodyBytes.Length > V3Layout.SlotBodyMax)
        {
            // truncate and set flag
            Array.Resize(ref bodyBytes, V3Layout.SlotBodyMax);
            flags |= ReaderFlags.Truncated;
        }

        int activeOff = activeSlot == 'A' ? V3Layout.SlotAOff : V3Layout.SlotBOff;
        WriteSlot(buf.AsSpan(activeOff, V3Layout.SlotLen), seq, frameTimeMs, flags, sec, bodyBytes);

        // Inactive slot — write a header with seq=0 so the scanner sees it as stale.
        int inactiveOff = activeSlot == 'A' ? V3Layout.SlotBOff : V3Layout.SlotAOff;
        WriteSlot(buf.AsSpan(inactiveOff, V3Layout.SlotLen), 0, 0, ReaderFlags.None, 0, []);

        return buf;
    }

    private static void WriteSlot(
        Span<byte> slot,
        ulong seq,
        long frameTimeMs,
        ReaderFlags flags,
        uint sec,
        ReadOnlySpan<byte> body)
    {
        // -- SLOT_HDR (64 bytes, fixed layout) --
        slot[0] = (byte)'S';
        slot[1] = (byte)'H';
        slot[2] = (byte)'|';
        V3Layout.WriteHex(slot.Slice(V3Layout.HdrSeqOff, 16), seq, 16);
        slot[19] = (byte)'|';
        V3Layout.WriteHex(slot.Slice(V3Layout.HdrTOff, 12), (ulong)frameTimeMs, 12);
        slot[32] = (byte)'|';
        V3Layout.WriteHex(slot.Slice(V3Layout.HdrFlagsOff, 8), (uint)flags, 8);
        slot[41] = (byte)'|';
        V3Layout.WriteHex(slot.Slice(V3Layout.HdrLenOff, 8), (uint)body.Length, 8);
        slot[50] = (byte)'|';
        V3Layout.WriteHex(slot.Slice(V3Layout.HdrSecOff, 8), sec, 8);
        slot[59] = (byte)'|';
        slot[60] = (byte)'0';
        slot[61] = V3Layout.VerByte;
        slot[62] = (byte)'|';
        slot[63] = (byte)'\n';

        // -- BODY --
        var bodyDest = slot.Slice(V3Layout.BodyOff, V3Layout.SlotBodyMax);
        body.CopyTo(bodyDest);
        bodyDest[body.Length..].Fill((byte)'.');

        // -- CRC over (SLOT_HDR + body[0..len]) --
        // Header is already at slot[0..64] and body is already at slot[64..64+len],
        // so we can CRC the contiguous slice directly — no copy needed.
        uint crc = Crc32.Compute(slot[..(V3Layout.SlotHdrLen + body.Length)]);
        V3Layout.WriteHex(slot.Slice(V3Layout.CrcOff, V3Layout.CrcLen), crc, 8);

        // -- SLOT_END --
        V3Layout.SlotEnd.CopyTo(slot.Slice(V3Layout.SlotEndOff, V3Layout.SlotEndLen));
    }

    private static uint BuildBody(StringBuilder sb, ReaderSnapshot snap)
    {
        uint sec = 0;

        // P — Player
        sec |= V3Layout.SecP;
        AppendSection(sb, 'P', body =>
        {
            AppendStr(body, "name", snap.Player.Name ?? "");
            AppendInt(body, "level", snap.Player.Level ?? 0);
            AppendStr(body, "calling", snap.Player.Calling ?? "");
            AppendStr(body, "guild", snap.Player.Guild ?? "");
            AppendInt(body, "hp", snap.Stats.Hp ?? 0);
            AppendInt(body, "hpMax", snap.Stats.HpMax ?? 0);
            AppendInt(body, "hpPct", snap.Stats.HpPercent ?? 0);
            AppendStr(body, "resKind", snap.Stats.ResourceKind ?? "");
            AppendInt(body, "resCur", snap.Stats.Resource ?? 0);
            AppendInt(body, "resMax", snap.Stats.ResourceMax ?? 0);
            AppendInt(body, "resPct", snap.Stats.ResourcePercent ?? 0);
            AppendFloat(body, "x", snap.Position.X ?? 0);
            AppendFloat(body, "y", snap.Position.Y ?? 0);
            AppendFloat(body, "z", snap.Position.Z ?? 0);
        });

        // T — Target
        if (snap.Target is not null)
        {
            sec |= V3Layout.SecT;
            AppendSection(sb, 'T', body =>
            {
                AppendStr(body, "name", snap.Target.Name ?? "");
                AppendInt(body, "level", snap.Target.Level ?? 0);
                AppendInt(body, "hpPct", snap.Target.HpPercent ?? 0);
                AppendStr(body, "rel", snap.Target.Relation ?? "");
            });
        }

        // Z — Zone
        if (snap.Zone is not null)
        {
            sec |= V3Layout.SecZ;
            AppendSection(sb, 'Z', body =>
            {
                AppendInt(body, "id", snap.Zone.Id);
                AppendStr(body, "name", snap.Zone.Name);
            });
        }

        // S — Stats
        if (snap.Combat is not null)
        {
            sec |= V3Layout.SecS;
            AppendSection(sb, 'S', body =>
            {
                AppendDouble(body, "dps1s", snap.Combat.Dps1s);
                AppendDouble(body, "dps5s", snap.Combat.Dps5s);
                AppendDouble(body, "hps1s", snap.Combat.Hps1s);
                AppendDouble(body, "hps5s", snap.Combat.Hps5s);
                AppendDouble(body, "in1s", snap.Combat.Incoming1s);
                AppendDouble(body, "in5s", snap.Combat.Incoming5s);
            });
        }

        // B / D / b / d — Buffs / Debuffs
        if (snap.PlayerBuffs is { Count: > 0 } pb)   { sec |= V3Layout.SecB; AppendBuffSection(sb, 'B', pb); }
        if (snap.PlayerDebuffs is { Count: > 0 } pd) { sec |= V3Layout.SecD; AppendBuffSection(sb, 'D', pd); }
        if (snap.TargetBuffs is { Count: > 0 } tb)   { sec |= V3Layout.Secb; AppendBuffSection(sb, 'b', tb); }
        if (snap.TargetDebuffs is { Count: > 0 } td) { sec |= V3Layout.Secd; AppendBuffSection(sb, 'd', td); }

        // C — Combat events
        if (snap.CombatEvents is { Count: > 0 } ce)
        {
            sec |= V3Layout.SecC;
            AppendCombatSection(sb, ce);
        }

        // K — Cooldowns (v4)
        if (snap.Cooldowns is { Count: > 0 } cds)
        {
            sec |= V3Layout.SecK;
            AppendCooldownSection(sb, cds);
        }

        // X — Character attributes (v4)
        if (snap.Attributes is not null)
        {
            sec |= V3Layout.SecX;
            AppendAttributesSection(sb, snap.Attributes);
        }

        // E — Equipment (v4)
        if (snap.Equipment is { Count: > 0 } eq)
        {
            sec |= V3Layout.SecE;
            AppendEquipmentSection(sb, eq);
        }

        // Y — Currency (v4)
        if (snap.Currencies is { Count: > 0 } cur)
        {
            sec |= V3Layout.SecY;
            AppendCurrencySection(sb, cur);
        }

        // G — Group/raid (v4)
        if (snap.Group is { Count: > 0 } grp)
        {
            sec |= V3Layout.SecG;
            AppendGroupSection(sb, grp);
        }

        return sec;
    }

    private static void AppendCooldownSection(StringBuilder sb, IReadOnlyList<CooldownInfo> cds)
    {
        AppendSection(sb, 'K', body =>
        {
            AppendInt(body, "n", cds.Count);
            for (int i = 0; i < cds.Count; i++)
            {
                var c = cds[i];
                AppendLong(body, "id", c.AbilityId);
                AppendStr(body, "nm", c.Name);
                AppendInt(body, "rem", c.RemainMs);
                AppendInt(body, "dur", c.DurationMs);
                AppendInt(body, "cost", c.ResourceCost);
                AppendStr(body, "rk", c.ResourceKind ?? "");
            }
        });
    }

    private static void AppendAttributesSection(StringBuilder sb, CharacterStats a)
    {
        AppendSection(sb, 'X', body =>
        {
            AppendInt(body, "str", a.Strength ?? 0);
            AppendInt(body, "dex", a.Dexterity ?? 0);
            AppendInt(body, "int", a.Intelligence ?? 0);
            AppendInt(body, "wis", a.Wisdom ?? 0);
            AppendInt(body, "end", a.Endurance ?? 0);
            AppendInt(body, "arm", a.Armor ?? 0);
            AppendInt(body, "dflCh", a.DeflectChance ?? 0);
            AppendInt(body, "dgCh", a.DodgeChance ?? 0);
            AppendInt(body, "pryCh", a.ParryChance ?? 0);
            AppendInt(body, "rAir", a.ResistAir ?? 0);
            AppendInt(body, "rDth", a.ResistDeath ?? 0);
            AppendInt(body, "rEth", a.ResistEarth ?? 0);
            AppendInt(body, "rFir", a.ResistFire ?? 0);
            AppendInt(body, "rLif", a.ResistLife ?? 0);
            AppendInt(body, "rWtr", a.ResistWater ?? 0);
            AppendInt(body, "cri", a.CritHit ?? 0);
            AppendInt(body, "hit", a.Hit ?? 0);
            AppendInt(body, "ap", a.AttackPower ?? 0);
            AppendInt(body, "sp", a.SpellPower ?? 0);
            AppendInt(body, "hstP", a.Physical ?? 0);
            AppendInt(body, "hstS", a.Spell ?? 0);
        });
    }

    private static void AppendEquipmentSection(StringBuilder sb, IReadOnlyList<EquipmentItem> eq)
    {
        AppendSection(sb, 'E', body =>
        {
            AppendInt(body, "n", eq.Count);
            for (int i = 0; i < eq.Count; i++)
            {
                var it = eq[i];
                AppendStr(body, "slot", it.Slot);
                AppendStr(body, "id", it.ItemId ?? "");
                AppendStr(body, "nm", it.Name);
                AppendStr(body, "rar", it.Rarity ?? "");
                AppendInt(body, "ilvl", it.ItemLevel ?? 0);
                AppendInt(body, "aug", it.Augments ?? 0);
            }
        });
    }

    private static void AppendCurrencySection(StringBuilder sb, IReadOnlyList<CurrencyEntry> cur)
    {
        AppendSection(sb, 'Y', body =>
        {
            AppendInt(body, "n", cur.Count);
            for (int i = 0; i < cur.Count; i++)
            {
                var c = cur[i];
                AppendStr(body, "id", c.Id);
                AppendStr(body, "nm", c.Name);
                AppendLong(body, "amt", c.Amount);
                AppendLong(body, "max", c.Max ?? 0);
            }
        });
    }

    private static void AppendGroupSection(StringBuilder sb, IReadOnlyList<GroupMember> grp)
    {
        AppendSection(sb, 'G', body =>
        {
            AppendInt(body, "n", grp.Count);
            for (int i = 0; i < grp.Count; i++)
            {
                var m = grp[i];
                AppendStr(body, "uid", m.UnitId);
                AppendStr(body, "nm", m.Name);
                AppendInt(body, "lvl", m.Level ?? 0);
                AppendStr(body, "call", m.Calling ?? "");
                AppendStr(body, "role", m.Role ?? "");
                AppendInt(body, "hpP", m.HpPercent ?? 0);
                AppendInt(body, "resP", m.ResourcePercent ?? 0);
                AppendInt(body, "on", m.IsOnline ? 1 : 0);
                AppendInt(body, "dead", m.IsDead ? 1 : 0);
                AppendStr(body, "zn", m.ZoneName ?? "");
            }
        });
    }

    private static void AppendSection(StringBuilder sb, char tag, Action<StringBuilder> writer)
    {
        // Reserve space for the 9-byte header (tag + 8-hex length), fill in the
        // length once the body is built.
        int headerStart = sb.Length;
        sb.Append(tag);
        sb.Append("00000000");
        int bodyStart = sb.Length;
        writer(sb);
        int bodyByteLen = Encoding.UTF8.GetByteCount(sb.ToString().AsSpan(bodyStart));
        // Patch the 8-hex length into bytes
        Span<byte> lenBytes = stackalloc byte[8];
        V3Layout.WriteHex(lenBytes, (uint)bodyByteLen, 8);
        for (int i = 0; i < 8; i++)
            sb[headerStart + 1 + i] = (char)lenBytes[i];
    }

    private static void AppendBuffSection(StringBuilder sb, char tag, IReadOnlyList<BuffInfo> buffs)
    {
        AppendSection(sb, tag, body =>
        {
            AppendInt(body, "n", buffs.Count);
            for (int i = 0; i < buffs.Count; i++)
            {
                var b = buffs[i];
                AppendInt(body, "id", b.Id);
                AppendStr(body, "nm", b.Name);
                AppendInt(body, "stk", b.Stacks);
                AppendInt(body, "rem", b.RemainMs);
                AppendInt(body, "self", b.CasterIsSelf ? 1 : 0);
            }
        });
    }

    private static void AppendCombatSection(StringBuilder sb, IReadOnlyList<CombatEvent> events)
    {
        AppendSection(sb, 'C', body =>
        {
            AppendInt(body, "n", events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                AppendLong(body, "t", e.T);
                AppendStr(body, "src", e.Src);
                AppendStr(body, "dst", e.Dst);
                AppendInt(body, "abi", e.AbilityId);
                AppendStr(body, "ty", e.Type);
                AppendInt(body, "amt", e.Amount);
                AppendInt(body, "crit", e.Crit ? 1 : 0);
                AppendInt(body, "abs", e.Absorb);
            }
        });
    }

    private static void AppendStr(StringBuilder sb, string key, string value)
    {
        int byteLen = Encoding.UTF8.GetByteCount(value);
        sb.Append(key).Append('=').Append(byteLen).Append(':').Append(value).Append(';');
    }

    private static void AppendInt(StringBuilder sb, string key, int value)
        => sb.Append(key).Append('=').Append(value).Append(';');

    private static void AppendLong(StringBuilder sb, string key, long value)
        => sb.Append(key).Append('=').Append(value).Append(';');

    private static void AppendFloat(StringBuilder sb, string key, float value)
        => sb.Append(key).Append('=')
             .Append(value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(';');

    private static void AppendDouble(StringBuilder sb, string key, double value)
        => sb.Append(key).Append('=')
             .Append(value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
}
