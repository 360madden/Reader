using Reader.Models;

namespace Reader.Core;

/// <summary>
/// Parses ReaderBridge v3 wire-format buffers into <see cref="ReaderSnapshot"/>.
///
/// The format is fixed-offset (see <see cref="V3Layout"/>):
///
///   [ MAGIC 8 ][ CONTROL 32 ][ SLOT_A 8272 ][ SLOT_B 8272 ]   = 16584 bytes
///
/// Parsing strategy:
///   1. Verify magic.
///   2. Read CONTROL block; pick the active slot.
///   3. Read that slot's fixed 64-byte header (struct-style, no tokenizing).
///   4. Cross-check slot.seq == control.seq (torn-read guard #1).
///   5. Verify CRC32 over (SLOT_HDR + body[0..len])           (guard #2).
///   6. Verify SLOT_END sentinel                              (guard #3).
///   7. Walk body sections by tag using the `sec` bitmask.
/// </summary>
public static class MarkerParser
{
    public static ReadOnlySpan<byte> PreferredStartBytes => V3Layout.Magic;
    public static ReadOnlySpan<byte> PreferredEndBytes   => V3Layout.SlotEnd;
    public static int PayloadLength => V3Layout.TotalLen;

    /// <summary>
    /// Parses a buffer that starts with the v3 magic. Returns null if the magic
    /// is missing, the buffer is too small, the CRC fails, or the sentinels do
    /// not match.
    /// </summary>
    public static ReaderSnapshot? ParseFromBuffer(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < V3Layout.TotalLen) return null;
        if (!buffer[..V3Layout.MagicLen].SequenceEqual(V3Layout.Magic)) return null;

        var control = buffer.Slice(V3Layout.ControlOff, V3Layout.ControlLen);

        byte active = control[V3Layout.CtrlActiveOff];
        if (active != (byte)'A' && active != (byte)'B') return null;

        if (!V3Layout.TryParseHexU64(control.Slice(V3Layout.CtrlSeqOff, 16), out ulong ctrlSeq)) return null;
        if (!V3Layout.TryParseHexU64(control.Slice(V3Layout.CtrlTOff, 12), out ulong ctrlT)) return null;

        int slotOff = active == (byte)'A' ? V3Layout.SlotAOff : V3Layout.SlotBOff;
        var slot = buffer.Slice(slotOff, V3Layout.SlotLen);

        // Slot header
        if (slot[0] != (byte)'S' || slot[1] != (byte)'H') return null;
        if (!V3Layout.TryParseHexU64(slot.Slice(V3Layout.HdrSeqOff, 16), out ulong slotSeq)) return null;
        if (slotSeq != ctrlSeq) return null; // torn-read guard #1

        if (!V3Layout.TryParseHexU64(slot.Slice(V3Layout.HdrTOff, 12), out ulong slotT)) return null;
        if (!V3Layout.TryParseHexU32(slot.Slice(V3Layout.HdrFlagsOff, 8), out uint flagsRaw)) return null;
        if (!V3Layout.TryParseHexU32(slot.Slice(V3Layout.HdrLenOff, 8), out uint bodyLen)) return null;
        if (!V3Layout.TryParseHexU32(slot.Slice(V3Layout.HdrSecOff, 8), out uint secMask)) return null;

        if (slot[V3Layout.HdrVerOff] != (byte)'0' || slot[V3Layout.HdrVerOff + 1] != V3Layout.VerByte) return null;

        if (bodyLen > V3Layout.SlotBodyMax) return null;

        var body = slot.Slice(V3Layout.BodyOff, (int)bodyLen);

        // CRC verify (guard #2): CRC over (slotHdr + body[0..len])
        // Header and body are already contiguous in the slot, so we can CRC
        // the slice directly — no copy needed.
        if (!V3Layout.TryParseHexU32(slot.Slice(V3Layout.CrcOff, V3Layout.CrcLen), out uint storedCrc)) return null;
        uint computed = Crc32.Compute(slot[..(V3Layout.SlotHdrLen + body.Length)]);
        if (computed != storedCrc) return null;

        // Sentinel (guard #3)
        if (!slot.Slice(V3Layout.SlotEndOff, V3Layout.SlotEndLen).SequenceEqual(V3Layout.SlotEnd)) return null;

        // Walk sections
        var sectioned = WalkSections(body, secMask);

        return new ReaderSnapshot(
            ReaderPayloadVersion.V4,
            sectioned.Player ?? new PlayerIdentity(null, null, null, null),
            sectioned.Stats  ?? new PlayerStats(null, null, null, null, null, null, null),
            sectioned.Position ?? new PlayerPosition(null, null, null),
            sectioned.Target,
            DateTimeOffset.UtcNow,
            Seq:           ctrlSeq,
            FrameTimeMs:   (long)slotT,
            Flags:         (ReaderFlags)flagsRaw,
            PlayerBuffs:   sectioned.PlayerBuffs,
            PlayerDebuffs: sectioned.PlayerDebuffs,
            TargetBuffs:   sectioned.TargetBuffs,
            TargetDebuffs: sectioned.TargetDebuffs,
            CombatEvents:  sectioned.CombatEvents,
            Combat:        sectioned.Combat,
            Zone:          sectioned.Zone,
            Cooldowns:     sectioned.Cooldowns,
            Attributes:    sectioned.Attributes,
            Equipment:     sectioned.Equipment,
            Currencies:    sectioned.Currencies,
            Group:         sectioned.Group);
    }

    private struct ParsedSections
    {
        public PlayerIdentity? Player;
        public PlayerStats? Stats;
        public PlayerPosition? Position;
        public TargetInfo? Target;
        public ZoneInfo? Zone;
        public CombatStats? Combat;
        public List<BuffInfo>? PlayerBuffs;
        public List<BuffInfo>? PlayerDebuffs;
        public List<BuffInfo>? TargetBuffs;
        public List<BuffInfo>? TargetDebuffs;
        public List<CombatEvent>? CombatEvents;
        // v4 phase-2 sections
        public List<CooldownInfo>? Cooldowns;
        public CharacterStats? Attributes;
        public List<EquipmentItem>? Equipment;
        public List<CurrencyEntry>? Currencies;
        public List<GroupMember>? Group;
    }

    private static ParsedSections WalkSections(ReadOnlySpan<byte> body, uint secMask)
    {
        var result = new ParsedSections();
        int pos = 0;
        while (pos < body.Length)
        {
            if (pos + 9 > body.Length) break;
            byte tag = body[pos];
            // Stop scanning if we hit padding (`.`).
            if (tag == (byte)'.') break;
            if (!V3Layout.TryParseHexU32(body.Slice(pos + 1, 8), out uint sectionLen)) break;
            int payloadStart = pos + 9;
            if (payloadStart + sectionLen > body.Length) break;
            var payload = body.Slice(payloadStart, (int)sectionLen);

            switch ((char)tag)
            {
                case 'P': ParsePlayer(payload, ref result); break;
                case 'T': ParseTarget(payload, ref result); break;
                case 'Z': ParseZone(payload, ref result); break;
                case 'S': ParseStats(payload, ref result); break;
                case 'B': result.PlayerBuffs   = ParseBuffList(payload); break;
                case 'D': result.PlayerDebuffs = ParseBuffList(payload); break;
                case 'b': result.TargetBuffs   = ParseBuffList(payload); break;
                case 'd': result.TargetDebuffs = ParseBuffList(payload); break;
                case 'C': result.CombatEvents  = ParseCombatList(payload); break;
                // v4 phase-2 sections
                case 'K': result.Cooldowns     = ParseCooldownList(payload); break;
                case 'X': result.Attributes    = ParseAttributes(payload); break;
                case 'E': result.Equipment     = ParseEquipmentList(payload); break;
                case 'Y': result.Currencies    = ParseCurrencyList(payload); break;
                case 'G': result.Group         = ParseGroupList(payload); break;
            }

            pos = payloadStart + (int)sectionLen;
        }
        _ = secMask; // mask is informational; the walker is self-describing.
        return result;
    }

    private static void ParsePlayer(ReadOnlySpan<byte> payload, ref ParsedSections result)
    {
        string? name = null, calling = null, guild = null, resKind = null;
        int? level = null, hp = null, hpMax = null, hpPct = null;
        int? resCur = null, resMax = null, resPct = null;
        float? x = null, y = null, z = null;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            switch (KeyOf(k))
            {
                case "name":    name    = V3SectionWalker.DecodeString(v); break;
                case "level":   level   = V3SectionWalker.DecodeInt(v);    break;
                case "calling": calling = V3SectionWalker.DecodeString(v); break;
                case "guild":   guild   = V3SectionWalker.DecodeString(v); break;
                case "hp":      hp      = V3SectionWalker.DecodeInt(v);    break;
                case "hpMax":   hpMax   = V3SectionWalker.DecodeInt(v);    break;
                case "hpPct":   hpPct   = V3SectionWalker.DecodeInt(v);    break;
                case "resKind": resKind = V3SectionWalker.DecodeString(v); break;
                case "resCur":  resCur  = V3SectionWalker.DecodeInt(v);    break;
                case "resMax":  resMax  = V3SectionWalker.DecodeInt(v);    break;
                case "resPct":  resPct  = V3SectionWalker.DecodeInt(v);    break;
                case "x":       x       = V3SectionWalker.DecodeFloat(v);  break;
                case "y":       y       = V3SectionWalker.DecodeFloat(v);  break;
                case "z":       z       = V3SectionWalker.DecodeFloat(v);  break;
            }
        }

        result.Player = new PlayerIdentity(
            string.IsNullOrEmpty(name) ? null : name,
            level,
            string.IsNullOrEmpty(calling) ? null : calling,
            string.IsNullOrEmpty(guild) ? null : guild);
        result.Stats = new PlayerStats(hp, hpMax, hpPct, string.IsNullOrEmpty(resKind) ? null : resKind, resCur, resMax, resPct);
        result.Position = new PlayerPosition(x, y, z);
    }

    private static void ParseTarget(ReadOnlySpan<byte> payload, ref ParsedSections result)
    {
        string? name = null, rel = null;
        int? level = null, hpPct = null;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            switch (KeyOf(k))
            {
                case "name":  name  = V3SectionWalker.DecodeString(v); break;
                case "level": level = V3SectionWalker.DecodeInt(v);    break;
                case "hpPct": hpPct = V3SectionWalker.DecodeInt(v);    break;
                case "rel":   rel   = V3SectionWalker.DecodeString(v); break;
            }
        }

        if (!string.IsNullOrEmpty(name))
            result.Target = new TargetInfo(name, level, hpPct, rel);
    }

    private static void ParseZone(ReadOnlySpan<byte> payload, ref ParsedSections result)
    {
        int id = 0;
        string name = "";
        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            switch (KeyOf(k))
            {
                case "id":   id   = V3SectionWalker.DecodeInt(v) ?? 0; break;
                case "name": name = V3SectionWalker.DecodeString(v);   break;
            }
        }
        result.Zone = new ZoneInfo(id, name);
    }

    private static void ParseStats(ReadOnlySpan<byte> payload, ref ParsedSections result)
    {
        double dps1 = 0, dps5 = 0, hps1 = 0, hps5 = 0, in1 = 0, in5 = 0;
        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            switch (KeyOf(k))
            {
                case "dps1s": dps1 = V3SectionWalker.DecodeDouble(v) ?? 0; break;
                case "dps5s": dps5 = V3SectionWalker.DecodeDouble(v) ?? 0; break;
                case "hps1s": hps1 = V3SectionWalker.DecodeDouble(v) ?? 0; break;
                case "hps5s": hps5 = V3SectionWalker.DecodeDouble(v) ?? 0; break;
                case "in1s":  in1  = V3SectionWalker.DecodeDouble(v) ?? 0; break;
                case "in5s":  in5  = V3SectionWalker.DecodeDouble(v) ?? 0; break;
            }
        }
        result.Combat = new CombatStats(dps1, dps5, hps1, hps5, in1, in5);
    }

    private static List<BuffInfo> ParseBuffList(ReadOnlySpan<byte> payload)
    {
        var list = new List<BuffInfo>();
        int? id = null, stacks = null, rem = null;
        bool self = false;
        string nm = "";
        bool inEntry = false;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            string key = KeyOf(k);
            if (key == "n") continue;
            if (key == "id")
            {
                if (inEntry)
                {
                    list.Add(new BuffInfo(id ?? 0, nm, stacks ?? 0, rem ?? 0, self));
                    nm = ""; stacks = null; rem = null; self = false;
                }
                id = V3SectionWalker.DecodeInt(v);
                inEntry = true;
            }
            else if (key == "nm")   nm     = V3SectionWalker.DecodeString(v);
            else if (key == "stk")  stacks = V3SectionWalker.DecodeInt(v);
            else if (key == "rem")  rem    = V3SectionWalker.DecodeInt(v);
            else if (key == "self") self   = V3SectionWalker.DecodeBool(v);
        }
        if (inEntry)
            list.Add(new BuffInfo(id ?? 0, nm, stacks ?? 0, rem ?? 0, self));
        return list;
    }

    private static List<CombatEvent> ParseCombatList(ReadOnlySpan<byte> payload)
    {
        var list = new List<CombatEvent>();
        long t = 0; int abi = 0, amt = 0, abs = 0;
        bool crit = false;
        string src = "", dst = "", ty = "";
        bool inEntry = false;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            string key = KeyOf(k);
            if (key == "n") continue;
            if (key == "t")
            {
                if (inEntry)
                {
                    list.Add(new CombatEvent(t, src, dst, abi, ty, amt, crit, abs));
                    src = ""; dst = ""; ty = ""; abi = 0; amt = 0; abs = 0; crit = false;
                }
                t = V3SectionWalker.DecodeLong(v) ?? 0;
                inEntry = true;
            }
            else if (key == "src")  src  = V3SectionWalker.DecodeString(v);
            else if (key == "dst")  dst  = V3SectionWalker.DecodeString(v);
            else if (key == "abi")  abi  = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "ty")   ty   = V3SectionWalker.DecodeString(v);
            else if (key == "amt")  amt  = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "crit") crit = V3SectionWalker.DecodeBool(v);
            else if (key == "abs")  abs  = V3SectionWalker.DecodeInt(v) ?? 0;
        }
        if (inEntry)
            list.Add(new CombatEvent(t, src, dst, abi, ty, amt, crit, abs));
        return list;
    }

    private static List<CooldownInfo> ParseCooldownList(ReadOnlySpan<byte> payload)
    {
        var list = new List<CooldownInfo>();
        long id = 0;
        int rem = 0, dur = 0, cost = 0;
        string nm = "", rk = "";
        bool inEntry = false;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            string key = KeyOf(k);
            if (key == "n") continue;
            if (key == "id")
            {
                if (inEntry)
                {
                    list.Add(new CooldownInfo(id, nm, rem, dur, cost, string.IsNullOrEmpty(rk) ? null : rk));
                    nm = ""; rk = ""; rem = 0; dur = 0; cost = 0;
                }
                id = V3SectionWalker.DecodeLong(v) ?? 0;
                inEntry = true;
            }
            else if (key == "nm")   nm   = V3SectionWalker.DecodeString(v);
            else if (key == "rem")  rem  = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "dur")  dur  = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "cost") cost = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "rk")   rk   = V3SectionWalker.DecodeString(v);
        }
        if (inEntry)
            list.Add(new CooldownInfo(id, nm, rem, dur, cost, string.IsNullOrEmpty(rk) ? null : rk));
        return list;
    }

    private static CharacterStats ParseAttributes(ReadOnlySpan<byte> payload)
    {
        int? str = null, dex = null, intel = null, wis = null, end = null;
        int? arm = null, dflCh = null, dgCh = null, pryCh = null;
        int? rAir = null, rDth = null, rEth = null, rFir = null, rLif = null, rWtr = null;
        int? cri = null, hit = null, ap = null, sp = null;
        int? hstP = null, hstS = null;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            switch (KeyOf(k))
            {
                case "str":   str   = V3SectionWalker.DecodeInt(v); break;
                case "dex":   dex   = V3SectionWalker.DecodeInt(v); break;
                case "int":   intel = V3SectionWalker.DecodeInt(v); break;
                case "wis":   wis   = V3SectionWalker.DecodeInt(v); break;
                case "end":   end   = V3SectionWalker.DecodeInt(v); break;
                case "arm":   arm   = V3SectionWalker.DecodeInt(v); break;
                case "dflCh": dflCh = V3SectionWalker.DecodeInt(v); break;
                case "dgCh":  dgCh  = V3SectionWalker.DecodeInt(v); break;
                case "pryCh": pryCh = V3SectionWalker.DecodeInt(v); break;
                case "rAir":  rAir  = V3SectionWalker.DecodeInt(v); break;
                case "rDth":  rDth  = V3SectionWalker.DecodeInt(v); break;
                case "rEth":  rEth  = V3SectionWalker.DecodeInt(v); break;
                case "rFir":  rFir  = V3SectionWalker.DecodeInt(v); break;
                case "rLif":  rLif  = V3SectionWalker.DecodeInt(v); break;
                case "rWtr":  rWtr  = V3SectionWalker.DecodeInt(v); break;
                case "cri":   cri   = V3SectionWalker.DecodeInt(v); break;
                case "hit":   hit   = V3SectionWalker.DecodeInt(v); break;
                case "ap":    ap    = V3SectionWalker.DecodeInt(v); break;
                case "sp":    sp    = V3SectionWalker.DecodeInt(v); break;
                case "hstP":  hstP  = V3SectionWalker.DecodeInt(v); break;
                case "hstS":  hstS  = V3SectionWalker.DecodeInt(v); break;
            }
        }

        return new CharacterStats(
            str, dex, intel, wis, end,
            arm, dflCh, dgCh, pryCh,
            rAir, rDth, rEth, rFir, rLif, rWtr,
            cri, hit, ap, sp,
            hstP, hstS);
    }

    private static List<EquipmentItem> ParseEquipmentList(ReadOnlySpan<byte> payload)
    {
        var list = new List<EquipmentItem>();
        string slot = "", id = "", nm = "", rar = "";
        int ilvl = 0, aug = 0;
        bool inEntry = false;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            string key = KeyOf(k);
            if (key == "n") continue;
            if (key == "slot")
            {
                if (inEntry)
                {
                    list.Add(new EquipmentItem(
                        slot,
                        string.IsNullOrEmpty(id) ? null : id,
                        nm,
                        string.IsNullOrEmpty(rar) ? null : rar,
                        ilvl,
                        aug));
                    slot = ""; id = ""; nm = ""; rar = ""; ilvl = 0; aug = 0;
                }
                slot = V3SectionWalker.DecodeString(v);
                inEntry = true;
            }
            else if (key == "id")   id   = V3SectionWalker.DecodeString(v);
            else if (key == "nm")   nm   = V3SectionWalker.DecodeString(v);
            else if (key == "rar")  rar  = V3SectionWalker.DecodeString(v);
            else if (key == "ilvl") ilvl = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "aug")  aug  = V3SectionWalker.DecodeInt(v) ?? 0;
        }
        if (inEntry)
            list.Add(new EquipmentItem(
                slot,
                string.IsNullOrEmpty(id) ? null : id,
                nm,
                string.IsNullOrEmpty(rar) ? null : rar,
                ilvl,
                aug));
        return list;
    }

    private static List<CurrencyEntry> ParseCurrencyList(ReadOnlySpan<byte> payload)
    {
        var list = new List<CurrencyEntry>();
        string id = "", nm = "";
        long amt = 0, max = 0;
        bool inEntry = false;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            string key = KeyOf(k);
            if (key == "n") continue;
            if (key == "id")
            {
                if (inEntry)
                {
                    list.Add(new CurrencyEntry(id, nm, amt, max == 0 ? null : max));
                    id = ""; nm = ""; amt = 0; max = 0;
                }
                id = V3SectionWalker.DecodeString(v);
                inEntry = true;
            }
            else if (key == "nm")  nm  = V3SectionWalker.DecodeString(v);
            else if (key == "amt") amt = V3SectionWalker.DecodeLong(v) ?? 0;
            else if (key == "max") max = V3SectionWalker.DecodeLong(v) ?? 0;
        }
        if (inEntry)
            list.Add(new CurrencyEntry(id, nm, amt, max == 0 ? null : max));
        return list;
    }

    private static List<GroupMember> ParseGroupList(ReadOnlySpan<byte> payload)
    {
        var list = new List<GroupMember>();
        string uid = "", nm = "", call = "", role = "", zn = "";
        int lvl = 0, hpP = 0, resP = 0;
        bool on = false, dead = false;
        bool inEntry = false;

        var w = new V3SectionWalker(payload);
        while (w.TryNext(out var k, out var v, out _))
        {
            string key = KeyOf(k);
            if (key == "n") continue;
            if (key == "uid")
            {
                if (inEntry)
                {
                    list.Add(new GroupMember(
                        uid, nm, lvl,
                        string.IsNullOrEmpty(call) ? null : call,
                        string.IsNullOrEmpty(role) ? null : role,
                        hpP, resP, on, dead,
                        string.IsNullOrEmpty(zn) ? null : zn));
                    uid = ""; nm = ""; call = ""; role = ""; zn = "";
                    lvl = 0; hpP = 0; resP = 0; on = false; dead = false;
                }
                uid = V3SectionWalker.DecodeString(v);
                inEntry = true;
            }
            else if (key == "nm")   nm   = V3SectionWalker.DecodeString(v);
            else if (key == "lvl")  lvl  = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "call") call = V3SectionWalker.DecodeString(v);
            else if (key == "role") role = V3SectionWalker.DecodeString(v);
            else if (key == "hpP")  hpP  = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "resP") resP = V3SectionWalker.DecodeInt(v) ?? 0;
            else if (key == "on")   on   = V3SectionWalker.DecodeBool(v);
            else if (key == "dead") dead = V3SectionWalker.DecodeBool(v);
            else if (key == "zn")   zn   = V3SectionWalker.DecodeString(v);
        }
        if (inEntry)
            list.Add(new GroupMember(
                uid, nm, lvl,
                string.IsNullOrEmpty(call) ? null : call,
                string.IsNullOrEmpty(role) ? null : role,
                hpP, resP, on, dead,
                string.IsNullOrEmpty(zn) ? null : zn));
        return list;
    }

    private static string KeyOf(ReadOnlySpan<byte> bytes)
        => System.Text.Encoding.ASCII.GetString(bytes);
}
