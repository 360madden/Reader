--[[
================================================================================
ReaderBridge v3 — high-performance Lua-side emitter for the RIFT Reader project.

Mirrors the byte layout defined in Reader.Core/V3Layout.cs exactly. The C#
scanner reads the global `ReaderBridge_v3` directly out of the RIFT process's
Lua string heap via ReadProcessMemory.

Layout (always exactly 8392 bytes):
    [ MAGIC 8 ][ CONTROL 32 ][ SLOT_A 4176 ][ SLOT_B 4176 ]

Design rules:
  * Event handlers only mark dirty flags; they NEVER publish.
  * A single Event.System.Update.End hook drains dirty flags at a 50 ms cadence
    (target ~20 Hz).
  * Body is built once per tick into a reused `buf` table and concat'd once.
  * Total published byte length is invariant, which lets Lua's string allocator
    reuse the same heap slot across publishes — the C# scanner's cached
    address will hit on most ticks with zero rescan cost.

WARNING: Do not reorder fields or change hex widths. The parser on the C# side
is offset-indexed; any drift will break the integrity guards.
================================================================================
]]

ReaderBridge = ReaderBridge or {}
ReaderBridge_v3 = ReaderBridge_v3 or ""
ReaderBridge_State = ReaderBridge_State or {}

local VERSION    = "0.3.0"
local TICK_MS    = 50           -- publish cadence in milliseconds (20 Hz)
local COMBAT_RING_SIZE = 64     -- cap on combat events drained per tick

-- ---------- hot-path locals (avoid global lookups in tight loops) ----------
local concat    = table.concat
local insert    = table.insert
local format    = string.format
local rep       = string.rep
local sbyte     = string.byte
local floor     = math.floor
local maxFn     = math.max

-- RIFT API locals (lookup once, reuse every tick)
local Inspect_Unit_Detail   = Inspect and Inspect.Unit and Inspect.Unit.Detail
local Inspect_Unit_Lookup   = Inspect and Inspect.Unit and Inspect.Unit.Lookup
local Inspect_Time_Frame    = Inspect and Inspect.Time and Inspect.Time.Frame
local Inspect_Buff_List     = Inspect and Inspect.Buff and Inspect.Buff.List
local Inspect_Buff_Detail   = Inspect and Inspect.Buff and Inspect.Buff.Detail

-- ---------- bit library (LuaJIT/bit, Lua 5.2/bit32, arithmetic fallback) ----
local bxor, band, rshift
if type(bit) == "table" and bit.bxor then
    bxor, band, rshift = bit.bxor, bit.band, bit.rshift
elseif type(bit32) == "table" and bit32.bxor then
    bxor, band, rshift = bit32.bxor, bit32.band, bit32.rshift
else
    -- Arithmetic fallback (only correct for the 32-bit range used by CRC32).
    local function _bxor(a, b)
        local r, p = 0, 1
        for _ = 1, 32 do
            if (a % 2) ~= (b % 2) then r = r + p end
            a = floor(a / 2); b = floor(b / 2); p = p * 2
        end
        return r
    end
    bxor   = _bxor
    band   = function(a, b) return a % (b + 1) end -- works only for b == 0xFF
    rshift = function(a, n) return floor(a / (2 ^ n)) end
end

-- ---------- CRC32 table (IEEE 802.3, reflected, matches Crc32.cs) ----------
local crcTable = {}
for i = 0, 255 do
    local c = i
    for _ = 1, 8 do
        if (c % 2) == 1 then
            c = bxor(floor(c / 2), 0xEDB88320)
        else
            c = floor(c / 2)
        end
    end
    crcTable[i] = c
end

local function crc32(s)
    local crc = 0xFFFFFFFF
    for i = 1, #s do
        local bv = sbyte(s, i)
        local idx = bxor(crc, bv) % 256
        crc = bxor(floor(crc / 256), crcTable[idx])
    end
    return bxor(crc, 0xFFFFFFFF)
end

-- ---------- hex format helpers (zero-padded, fixed width) ----------
-- Lua 5.1 doubles safely represent integers up to 2^53. Seq and timestamps
-- stay well under that bound for centuries of 20 Hz operation.

local function hex8(n)
    return format("%08X", n % 0x100000000)
end

local function hex12(n)
    -- 48-bit value. Split into high 16 + low 32.
    local low  = n % 0x100000000
    local high = floor(n / 0x100000000) % 0x10000
    return format("%04X%08X", high, low)
end

local function hex16(n)
    -- 64-bit value. Precision is limited to 2^53 by IEEE doubles, which is
    -- sufficient for a monotonic tick counter.
    local low  = n % 0x100000000
    local high = floor(n / 0x100000000) % 0x100000000
    return format("%08X%08X", high, low)
end

-- ---------- protocol constants (must match V3Layout.cs) ----------
local MAGIC         = "RBRG3\n=="   -- 8 bytes
local SLOT_END      = "==RBRG=="    -- 8 bytes
local MAGIC_LEN     = 8
local CONTROL_LEN   = 32
local SLOT_HDR_LEN  = 64
local SLOT_BODY_MAX = 4096
local CRC_LEN       = 8
local SLOT_END_LEN  = 8
local SLOT_LEN      = SLOT_HDR_LEN + SLOT_BODY_MAX + CRC_LEN + SLOT_END_LEN -- 4176
local TOTAL_LEN     = MAGIC_LEN + CONTROL_LEN + 2 * SLOT_LEN                -- 8392

-- Padded empty body used by the inactive slot on first publish.
local EMPTY_BODY_PAD = rep(".", SLOT_BODY_MAX)

-- Flag bits (match ReaderFlags.cs)
local FLAG_IN_COMBAT   = 0x01
local FLAG_HAS_TARGET  = 0x02
local FLAG_GROUPED     = 0x04
local FLAG_RESTING     = 0x08
local FLAG_MOUNTED     = 0x10
local FLAG_PARTIAL     = 0x20
local FLAG_TRUNCATED   = 0x40

-- Section presence bits (match V3Layout.cs)
local SEC_P = 0x001
local SEC_T = 0x002
local SEC_B = 0x004
local SEC_D = 0x008
local SEC_b = 0x010
local SEC_d = 0x020
local SEC_C = 0x040
local SEC_S = 0x080
local SEC_Z = 0x100

-- ---------- state store ----------
local state = {
    player = {
        dirty = true,
        name = "", level = 0, calling = "", guild = "",
        hp = 0, hpMax = 0, hpPct = 0,
        resKind = "", resCur = 0, resMax = 0, resPct = 0,
        x = 0, y = 0, z = 0,
        inCombat = false, mounted = false, resting = false,
    },
    target = {
        dirty = false, exists = false,
        name = "", level = 0, hpPct = 0, relation = "",
    },
    zone = {
        dirty = false, id = 0, name = "",
    },
    playerBuffs   = { dirty = false, list = {} },
    playerDebuffs = { dirty = false, list = {} },
    targetBuffs   = { dirty = false, list = {} },
    targetDebuffs = { dirty = false, list = {} },
    combatRing    = { dirty = false, head = 1, tail = 1, count = 0, buf = {} },
    combatStats   = {
        dirty = false,
        dps1s = 0, dps5s = 0, hps1s = 0, hps5s = 0,
        in1s = 0, in5s = 0,
    },
}

local playerUnitID = nil
local targetUnitID = nil

-- ---------- safe inspection wrappers ----------
local function safeDetail(spec)
    if not Inspect_Unit_Detail then return nil end
    local ok, r = pcall(Inspect_Unit_Detail, spec)
    if ok then return r end
    return nil
end

local function safeLookup(spec)
    if not Inspect_Unit_Lookup then return nil end
    local ok, r = pcall(Inspect_Unit_Lookup, spec)
    if ok and r and r ~= false then return r end
    return nil
end

local function frameTimeMs()
    if not Inspect_Time_Frame then return 0 end
    local ok, r = pcall(Inspect_Time_Frame)
    if ok and type(r) == "number" then return floor(r * 1000) end
    return 0
end

-- ---------- resource kind detection ----------
local function readResource(detail)
    if detail.manaMax and detail.manaMax > 0 then
        return "mana", detail.mana or 0, detail.manaMax
    elseif detail.energyMax and detail.energyMax > 0 then
        return "energy", detail.energy or 0, detail.energyMax
    elseif detail.power ~= nil then
        return "power", detail.power or 0, detail.powerMax or 100
    elseif detail.chargeMax and detail.chargeMax > 0 then
        return "charge", detail.charge or 0, detail.chargeMax
    elseif detail.combo ~= nil then
        return "combo", detail.combo or 0, detail.comboMax or 5
    end
    return "", 0, 0
end

local function pct(cur, max)
    if not max or max <= 0 then return 0 end
    local p = floor(((cur / max) * 100) + 0.5)
    if p < 0 then return 0 end
    if p > 100 then return 100 end
    return p
end

-- ---------- state refresh (called inside Tick, not from events) ----------
local function refreshPlayer()
    local d = safeDetail("player")
    local p = state.player
    if not d then
        p.name = ""; p.level = 0; p.calling = ""; p.guild = ""
        p.hp = 0; p.hpMax = 0; p.hpPct = 0
        p.resKind = ""; p.resCur = 0; p.resMax = 0; p.resPct = 0
        p.x = 0; p.y = 0; p.z = 0
        p.inCombat = false; p.mounted = false; p.resting = false
        return false
    end

    p.name    = d.name or ""
    p.level   = d.level or 0
    p.calling = d.calling or ""
    p.guild   = d.guild or ""
    p.hp      = d.health or 0
    p.hpMax   = d.healthMax or 0
    p.hpPct   = pct(p.hp, p.hpMax)

    local rk, rc, rm = readResource(d)
    p.resKind = rk; p.resCur = rc; p.resMax = rm; p.resPct = pct(rc, rm)

    p.x = d.coordX or 0
    p.y = d.coordY or 0
    p.z = d.coordZ or 0

    p.inCombat = d.combat and true or false
    p.mounted  = d.mount and true or false
    p.resting  = false  -- RIFT has no direct equivalent; reserved
    return true
end

local function resolveTarget()
    local t = safeLookup("player.target")
    local changed = (t ~= targetUnitID)
    targetUnitID = t
    if changed then state.target.dirty = true end
    return changed
end

local function refreshTarget()
    local t = state.target
    if not targetUnitID then
        if t.exists then t.dirty = true end
        t.exists = false
        t.name = ""; t.level = 0; t.hpPct = 0; t.relation = ""
        return
    end
    local d = safeDetail(targetUnitID)
    if not d then
        t.exists = false
        t.name = ""; t.level = 0; t.hpPct = 0; t.relation = ""
        return
    end
    t.exists   = true
    t.name     = d.name or ""
    t.level    = d.level or 0
    t.hpPct    = pct(d.health or 0, d.healthMax or 0)
    t.relation = d.relation or ""
end

-- ---------- buff snapshot ----------
local function snapshotBuffsFor(unit, debuffs)
    if not unit or not Inspect_Buff_List or not Inspect_Buff_Detail then
        return {}
    end
    local ok, ids = pcall(Inspect_Buff_List, unit)
    if not ok or type(ids) ~= "table" then return {} end
    local result = {}
    for buffId in pairs(ids) do
        local ok2, d = pcall(Inspect_Buff_Detail, unit, buffId)
        if ok2 and d then
            local isDebuff = d.debuff and true or false
            if isDebuff == debuffs then
                local remain = 0
                if d.remaining and type(d.remaining) == "number" then
                    remain = floor(d.remaining * 1000)
                end
                result[#result + 1] = {
                    id     = tonumber(buffId) or 0,
                    name   = d.name or "",
                    stacks = d.stack or 1,
                    remain = remain,
                    self   = d.caster == playerUnitID,
                }
            end
        end
    end
    return result
end

local function refreshBuffs()
    state.playerBuffs.list   = snapshotBuffsFor(playerUnitID, false)
    state.playerDebuffs.list = snapshotBuffsFor(playerUnitID, true)
    if targetUnitID then
        state.targetBuffs.list   = snapshotBuffsFor(targetUnitID, false)
        state.targetDebuffs.list = snapshotBuffsFor(targetUnitID, true)
    else
        state.targetBuffs.list   = {}
        state.targetDebuffs.list = {}
    end
end

-- ---------- combat ring buffer ----------
local function pushCombatEvent(t, src, dst, abi, ty, amt, crit, absorb)
    local r = state.combatRing
    r.buf[r.tail] = { t = t, src = src or "", dst = dst or "",
                      abi = abi or 0, ty = ty or "", amt = amt or 0,
                      crit = crit and true or false, absorb = absorb or 0 }
    r.tail = r.tail + 1
    if r.count < COMBAT_RING_SIZE then
        r.count = r.count + 1
    else
        r.head = r.head + 1
    end
    r.dirty = true
end

local function drainCombatRing()
    local r = state.combatRing
    local events = {}
    local idx = r.head
    for _ = 1, r.count do
        events[#events + 1] = r.buf[idx]
        r.buf[idx] = nil
        idx = idx + 1
    end
    r.head = 1; r.tail = 1; r.count = 0
    r.dirty = false
    return events
end

-- ---------- simple rolling DPS/HPS (1s + 5s windows) ----------
local dpsAccum1, dpsAccum5 = {}, {}
local hpsAccum1, hpsAccum5 = {}, {}
local inAccum1, inAccum5   = {}, {}

local function pushWindow(window, now, value)
    window[#window + 1] = { now, value }
end

local function sumWindow(window, now, cutoffMs)
    local cutoff = now - cutoffMs
    local sum = 0
    local i = 1
    while i <= #window do
        if window[i][1] < cutoff then
            table.remove(window, i)
        else
            sum = sum + window[i][2]
            i = i + 1
        end
    end
    return sum
end

local function recomputeCombatStats(nowMs)
    local s = state.combatStats
    s.dps1s = sumWindow(dpsAccum1, nowMs, 1000)
    s.dps5s = sumWindow(dpsAccum5, nowMs, 5000) / 5
    s.hps1s = sumWindow(hpsAccum1, nowMs, 1000)
    s.hps5s = sumWindow(hpsAccum5, nowMs, 5000) / 5
    s.in1s  = sumWindow(inAccum1, nowMs, 1000)
    s.in5s  = sumWindow(inAccum5, nowMs, 5000) / 5
    s.dirty = true
end

-- ---------- section builders ----------
-- All appenders write into a shared content `buf` table; the wrapper
-- emitSection then computes the byte length and prepends the TAG + hex8 len.

local function putStr(buf, key, value)
    local s = value or ""
    buf[#buf + 1] = key
    buf[#buf + 1] = "="
    buf[#buf + 1] = tostring(#s)
    buf[#buf + 1] = ":"
    buf[#buf + 1] = s
    buf[#buf + 1] = ";"
end

local function putInt(buf, key, value)
    buf[#buf + 1] = key
    buf[#buf + 1] = "="
    buf[#buf + 1] = tostring(floor(value or 0))
    buf[#buf + 1] = ";"
end

local function putBool(buf, key, value)
    buf[#buf + 1] = key
    buf[#buf + 1] = "="
    buf[#buf + 1] = value and "1" or "0"
    buf[#buf + 1] = ";"
end

local function putFloat(buf, key, value)
    buf[#buf + 1] = key
    buf[#buf + 1] = "="
    buf[#buf + 1] = format("%.2f", value or 0)
    buf[#buf + 1] = ";"
end

local function emitSection(bodyBuf, tag, contentBuf)
    local content = concat(contentBuf)
    bodyBuf[#bodyBuf + 1] = tag
    bodyBuf[#bodyBuf + 1] = hex8(#content)
    bodyBuf[#bodyBuf + 1] = content
end

local function buildPlayerSection(bodyBuf)
    local p = state.player
    local c = {}
    putStr(c,   "name", p.name)
    putInt(c,   "level", p.level)
    putStr(c,   "calling", p.calling)
    putStr(c,   "guild", p.guild)
    putInt(c,   "hp", p.hp)
    putInt(c,   "hpMax", p.hpMax)
    putInt(c,   "hpPct", p.hpPct)
    putStr(c,   "resKind", p.resKind)
    putInt(c,   "resCur", p.resCur)
    putInt(c,   "resMax", p.resMax)
    putInt(c,   "resPct", p.resPct)
    putFloat(c, "x", p.x)
    putFloat(c, "y", p.y)
    putFloat(c, "z", p.z)
    emitSection(bodyBuf, "P", c)
end

local function buildTargetSection(bodyBuf)
    local t = state.target
    local c = {}
    putStr(c, "name", t.name)
    putInt(c, "level", t.level)
    putInt(c, "hpPct", t.hpPct)
    putStr(c, "rel", t.relation)
    emitSection(bodyBuf, "T", c)
end

local function buildZoneSection(bodyBuf)
    local z = state.zone
    local c = {}
    putInt(c, "id", z.id)
    putStr(c, "name", z.name)
    emitSection(bodyBuf, "Z", c)
end

local function buildStatsSection(bodyBuf)
    local s = state.combatStats
    local c = {}
    putFloat(c, "dps1s", s.dps1s)
    putFloat(c, "dps5s", s.dps5s)
    putFloat(c, "hps1s", s.hps1s)
    putFloat(c, "hps5s", s.hps5s)
    putFloat(c, "in1s",  s.in1s)
    putFloat(c, "in5s",  s.in5s)
    emitSection(bodyBuf, "S", c)
end

local function buildBuffSection(bodyBuf, tag, list)
    local c = {}
    putInt(c, "n", #list)
    for i = 1, #list do
        local b = list[i]
        putInt(c, "id",   b.id)
        putStr(c, "nm",   b.name)
        putInt(c, "stk",  b.stacks)
        putInt(c, "rem",  b.remain)
        putBool(c, "self", b.self)
    end
    emitSection(bodyBuf, tag, c)
end

local function buildCombatSection(bodyBuf, events)
    local c = {}
    putInt(c, "n", #events)
    for i = 1, #events do
        local e = events[i]
        putInt(c, "t", e.t)
        putStr(c, "src", e.src)
        putStr(c, "dst", e.dst)
        putInt(c, "abi", e.abi)
        putStr(c, "ty", e.ty)
        putInt(c, "amt", e.amt)
        putBool(c, "crit", e.crit)
        putInt(c, "abs", e.absorb)
    end
    emitSection(bodyBuf, "C", c)
end

-- ---------- slot assembly ----------

local function buildSlotHeader(seq, tMs, flags, bodyLen, secMask)
    -- Exactly 64 bytes. Layout mirrors V3Layout.cs HdrXxxOff constants.
    return "SH|"
        .. hex16(seq)     .. "|"   -- 16 + 1
        .. hex12(tMs)     .. "|"   -- 12 + 1
        .. hex8(flags)    .. "|"   -- 8 + 1
        .. hex8(bodyLen)  .. "|"   -- 8 + 1
        .. hex8(secMask)  .. "|"   -- 8 + 1
        .. "03|\n"                  -- 2 + 1 + 1
    -- 3 + 17 + 13 + 9 + 9 + 9 + 4 = 64
end

local function buildSlot(seq, tMs, flags, body, secMask)
    local bodyLen = #body
    if bodyLen > SLOT_BODY_MAX then
        -- Truncate oversize bodies and set the truncation flag.
        body = body:sub(1, SLOT_BODY_MAX)
        bodyLen = SLOT_BODY_MAX
        flags = flags + FLAG_TRUNCATED
    end

    local hdr = buildSlotHeader(seq, tMs, flags, bodyLen, secMask)
    if #hdr ~= SLOT_HDR_LEN then
        -- Should never happen; indicates hex helper drift. Fail loudly.
        error("ReaderBridge: slot header length " .. #hdr .. " != 64")
    end

    local crc    = hex8(crc32(hdr .. body))
    local padded = body .. rep(".", SLOT_BODY_MAX - bodyLen)
    return hdr .. padded .. crc .. SLOT_END
end

local function buildControl(active, seq, tMs)
    -- Exactly 32 bytes.
    return active .. "|" .. hex16(seq) .. "|" .. hex12(tMs) .. "\n"
end

-- Empty slot for first-publish inactive side (seq=0, everything zero).
local function buildEmptySlot()
    local hdr = buildSlotHeader(0, 0, 0, 0, 0)
    return hdr .. EMPTY_BODY_PAD .. hex8(crc32(hdr)) .. SLOT_END
end

-- ---------- publish state ----------
local seq          = 0
local lastPublishMs = 0
local lastTickAt   = 0
local activeSlot   = "A"       -- slot that currently holds the freshest data
local cachedSlotA  = nil
local cachedSlotB  = nil
local publishDurMs = 0         -- last publish cost (for the GUI indicator)

-- ---------- dirty test ----------
local function anyDirty()
    return state.player.dirty
        or state.target.dirty
        or state.zone.dirty
        or state.playerBuffs.dirty
        or state.playerDebuffs.dirty
        or state.targetBuffs.dirty
        or state.targetDebuffs.dirty
        or state.combatRing.dirty
        or state.combatStats.dirty
end

local function clearDirty()
    state.player.dirty        = false
    state.target.dirty        = false
    state.zone.dirty          = false
    state.playerBuffs.dirty   = false
    state.playerDebuffs.dirty = false
    state.targetBuffs.dirty   = false
    state.targetDebuffs.dirty = false
    state.combatStats.dirty   = false
    -- combatRing cleared by drainCombatRing()
end

-- ---------- main publish ----------
local function publish()
    local tStart = frameTimeMs()

    refreshPlayer()
    resolveTarget()
    refreshTarget()
    refreshBuffs()
    if state.combatRing.dirty then
        recomputeCombatStats(tStart)
    end

    -- Assemble body
    local body = {}
    local secMask = 0

    buildPlayerSection(body); secMask = secMask + SEC_P

    if state.target.exists then
        buildTargetSection(body); secMask = secMask + SEC_T
    end

    if state.zone.id ~= 0 or state.zone.name ~= "" then
        buildZoneSection(body); secMask = secMask + SEC_Z
    end

    buildStatsSection(body); secMask = secMask + SEC_S

    if #state.playerBuffs.list > 0 then
        buildBuffSection(body, "B", state.playerBuffs.list); secMask = secMask + SEC_B
    end
    if #state.playerDebuffs.list > 0 then
        buildBuffSection(body, "D", state.playerDebuffs.list); secMask = secMask + SEC_D
    end
    if #state.targetBuffs.list > 0 then
        buildBuffSection(body, "b", state.targetBuffs.list); secMask = secMask + SEC_b
    end
    if #state.targetDebuffs.list > 0 then
        buildBuffSection(body, "d", state.targetDebuffs.list); secMask = secMask + SEC_d
    end

    if state.combatRing.count > 0 then
        buildCombatSection(body, drainCombatRing()); secMask = secMask + SEC_C
    end

    local bodyStr = concat(body)

    -- Compute flags
    local flags = 0
    if state.player.inCombat then flags = flags + FLAG_IN_COMBAT end
    if state.target.exists   then flags = flags + FLAG_HAS_TARGET end
    if state.player.mounted  then flags = flags + FLAG_MOUNTED end
    if state.player.resting  then flags = flags + FLAG_RESTING end

    -- Bump sequence
    seq = seq + 1

    -- Flip active slot: write the new slot onto the inactive side
    local nextActive = (activeSlot == "A") and "B" or "A"
    local newSlot = buildSlot(seq, tStart, flags, bodyStr, secMask)

    if nextActive == "A" then
        cachedSlotA = newSlot
        if not cachedSlotB then cachedSlotB = buildEmptySlot() end
    else
        cachedSlotB = newSlot
        if not cachedSlotA then cachedSlotA = buildEmptySlot() end
    end
    activeSlot = nextActive

    local control = buildControl(activeSlot, seq, tStart)

    -- Length safety-check: writing a wrong-length string silently would break
    -- the C# scanner's fixed-offset assumptions. Bail loudly.
    local full = MAGIC .. control .. cachedSlotA .. cachedSlotB
    if #full ~= TOTAL_LEN then
        error(format("ReaderBridge: total length %d != %d", #full, TOTAL_LEN))
    end

    ReaderBridge_v3   = full
    ReaderBridge_State = state

    publishDurMs = frameTimeMs() - tStart
    lastPublishMs = tStart
    clearDirty()
end

-- ---------- tick scheduler ----------
local function tick()
    local now = frameTimeMs()
    if now - lastTickAt < TICK_MS then return end
    lastTickAt = now

    -- Refresh target/player identity occasionally in case we missed an event.
    if not playerUnitID then
        playerUnitID = safeLookup("player")
    end

    if anyDirty() or (now - lastPublishMs) > 1000 then
        publish()
    end

    updateGuiIndicators(now)  -- forward decl; defined in GUI block below
end

-- ---------- event handlers (dirty flag only) ----------
local function onPlayerEvent(units)
    if playerUnitID and units and units[playerUnitID] then
        state.player.dirty = true
    end
end

local function onTargetEvent(units)
    -- Resolve lazily during Tick; here we just mark dirty if the current
    -- target's unit is among the changed set.
    if targetUnitID and units and units[targetUnitID] then
        state.target.dirty = true
    end
    -- Target may have changed even if we don't know about it yet; also mark
    -- on any player detail update (cheap) to pick up player.target swaps.
    state.target.dirty = true
end

local function onAvailabilityFull(units)
    if not playerUnitID then playerUnitID = safeLookup("player") end
    if playerUnitID and units and units[playerUnitID] then
        state.player.dirty = true
    end
    state.target.dirty = true
    state.playerBuffs.dirty = true
    state.playerDebuffs.dirty = true
end

local function onAvailabilityNone(units)
    if playerUnitID and units and units[playerUnitID] then
        state.player.dirty = true
    end
    if targetUnitID and units and units[targetUnitID] then
        targetUnitID = nil
        state.target.dirty = true
    end
end

local function onBuffEvent(unit, _buffs)
    if unit == playerUnitID then
        state.playerBuffs.dirty = true
        state.playerDebuffs.dirty = true
    elseif unit == targetUnitID then
        state.targetBuffs.dirty = true
        state.targetDebuffs.dirty = true
    end
end

local function onCombatDamage(info)
    if type(info) ~= "table" then return end
    local now = frameTimeMs()
    local amt = info.damage or info.amount or 0
    pushCombatEvent(now, info.caster or "", info.target or "",
                    info.ability or 0, info.type or "dmg", amt,
                    info.crit or false, info.absorbed or 0)
    if info.caster == playerUnitID then
        pushWindow(dpsAccum1, now, amt); pushWindow(dpsAccum5, now, amt)
    end
    if info.target == playerUnitID then
        pushWindow(inAccum1, now, amt); pushWindow(inAccum5, now, amt)
    end
    state.combatRing.dirty = true
end

local function onCombatHeal(info)
    if type(info) ~= "table" then return end
    local now = frameTimeMs()
    local amt = info.heal or info.amount or 0
    pushCombatEvent(now, info.caster or "", info.target or "",
                    info.ability or 0, "heal", amt, info.crit or false, 0)
    if info.caster == playerUnitID then
        pushWindow(hpsAccum1, now, amt); pushWindow(hpsAccum5, now, amt)
    end
    state.combatRing.dirty = true
end

-- ============================================================================
-- GUI status panel
-- ============================================================================
local guiCtx    = UI.CreateContext("ReaderBridgeGUI")
local guiFrame  = UI.CreateFrame("Frame", "RBGuiFrame", guiCtx)
guiFrame:SetPoint("TOPRIGHT", UIParent, "TOPRIGHT", -20, 20)
guiFrame:SetWidth(220)
guiFrame:SetHeight(46)
guiFrame:SetBackgroundColor(0.05, 0.05, 0.08, 0.88)
guiFrame:SetVisible(true)

local guiBorder = UI.CreateFrame("Frame", "RBGuiBorder", guiFrame)
guiBorder:SetPoint("TOPLEFT", guiFrame, "TOPLEFT", 0, 0)
guiBorder:SetPoint("TOPRIGHT", guiFrame, "TOPRIGHT", 0, 0)
guiBorder:SetHeight(18)
guiBorder:SetBackgroundColor(0.15, 0.15, 0.22, 1)

local guiTitle = UI.CreateFrame("Text", "RBGuiTitle", guiBorder)
guiTitle:SetPoint("TOPLEFT", guiBorder, "TOPLEFT", 6, 1)
guiTitle:SetText("RB v" .. VERSION)
guiTitle:SetFontSize(11)
guiTitle:SetFontColor(0.85, 0.85, 1.0, 1)

local guiSeq = UI.CreateFrame("Text", "RBGuiSeq", guiBorder)
guiSeq:SetPoint("TOPRIGHT", guiBorder, "TOPRIGHT", -6, 1)
guiSeq:SetText("seq:0")
guiSeq:SetFontSize(10)
guiSeq:SetFontColor(0.65, 0.85, 1.0, 1)

local lightNames = { "Tick", "Pub", "Plr", "Tgt", "Cbt" }
local lights = {}
local lightLabels = {}
for i = 1, 5 do
    local lt = UI.CreateFrame("Frame", "RBLight" .. i, guiFrame)
    lt:SetPoint("TOPLEFT", guiFrame, "TOPLEFT", 6 + (i - 1) * 42, 22)
    lt:SetWidth(10)
    lt:SetHeight(10)
    lt:SetBackgroundColor(0.3, 0.3, 0.3, 1)
    lights[i] = lt

    local lbl = UI.CreateFrame("Text", "RBLbl" .. i, guiFrame)
    lbl:SetPoint("TOPLEFT", lt, "TOPRIGHT", 2, -2)
    lbl:SetText(lightNames[i])
    lbl:SetFontSize(9)
    lbl:SetFontColor(0.7, 0.7, 0.75, 1)
    lightLabels[i] = lbl
end

local function setLight(i, color)
    local lt = lights[i]
    if not lt then return end
    if color == "green" then
        lt:SetBackgroundColor(0.18, 0.85, 0.22, 1)
    elseif color == "yellow" then
        lt:SetBackgroundColor(0.95, 0.78, 0.10, 1)
    elseif color == "red" then
        lt:SetBackgroundColor(0.90, 0.18, 0.18, 1)
    else
        lt:SetBackgroundColor(0.28, 0.28, 0.32, 1)
    end
end

local lastGuiSeq = -1
-- Defined earlier as forward-reference in tick(); real definition here:
function updateGuiIndicators(nowMs)
    -- 1. Tick
    local dtTick = nowMs - lastTickAt
    if dtTick <= 100 then setLight(1, "green")
    elseif dtTick <= 500 then setLight(1, "yellow")
    else setLight(1, "red") end

    -- 2. Publish duration
    if publishDurMs <= 1 then setLight(2, "green")
    elseif publishDurMs <= 3 then setLight(2, "yellow")
    else setLight(2, "red") end

    -- 3. Player
    if state.player.name ~= "" then setLight(3, "green")
    else setLight(3, "red") end

    -- 4. Target
    if state.target.exists then setLight(4, "green")
    else setLight(4, "grey") end

    -- 5. Combat
    if state.player.inCombat then setLight(5, "red")
    else setLight(5, "green") end

    if seq ~= lastGuiSeq then
        lastGuiSeq = seq
        guiSeq:SetText("seq:" .. tostring(seq))
    end
end

-- Drag (same pattern as PlayerCoords lines 73-88)
guiBorder:EventAttach(Event.UI.Input.Mouse.Left.Down, function(self)
    local m = Inspect.Mouse()
    self.drag = true
    self.ox = m.x - guiFrame:GetLeft()
    self.oy = m.y - guiFrame:GetTop()
end, "RBGuiDown")

guiBorder:EventAttach(Event.UI.Input.Mouse.Left.Up, function(self)
    self.drag = false
end, "RBGuiUp")

guiBorder:EventAttach(Event.UI.Input.Mouse.Cursor.Move, function(self)
    if not self.drag then return end
    local m = Inspect.Mouse()
    guiFrame:ClearAll()
    guiFrame:SetPoint("TOPLEFT", UIParent, "TOPLEFT", m.x - self.ox, m.y - self.oy)
end, "RBGuiMove")

-- ============================================================================
-- Slash commands
-- ============================================================================
local function slashDump()
    local lines = {
        "<font color='#00CCFF'>[ReaderBridge v" .. VERSION .. "]</font>",
        "seq=" .. seq .. " active=" .. activeSlot .. " publishDur=" .. publishDurMs .. "ms",
        "player=" .. state.player.name .. " lvl " .. state.player.level,
        "target=" .. (state.target.exists and state.target.name or "(none)"),
        "buffs=" .. #state.playerBuffs.list .. " debuffs=" .. #state.playerDebuffs.list,
        "combat.events=" .. state.combatRing.count,
        "payload.len=" .. #ReaderBridge_v3,
    }
    for _, line in ipairs(lines) do
        Command.Console.Display("general", false, line, false)
    end
end

local function slashStat()
    Command.Console.Display("general", false,
        format("<font color='#00CCFF'>[RB]</font> seq=%d tick=%dms publish=%dms tickInterval=%dms",
               seq, lastTickAt, publishDurMs, TICK_MS),
        false)
end

local function slashGui(args)
    local arg = (args and args[1] or ""):lower()
    if arg == "hide" then
        guiFrame:SetVisible(false)
    elseif arg == "show" then
        guiFrame:SetVisible(true)
    elseif arg == "reset" then
        guiFrame:ClearAll()
        guiFrame:SetPoint("TOPRIGHT", UIParent, "TOPRIGHT", -20, 20)
    else
        guiFrame:SetVisible(not guiFrame:GetVisible())
    end
end

insert(Command.Slash.Register("readerdump"), { slashDump, "ReaderBridge", "dump v3 state" })
insert(Command.Slash.Register("readerstat"), { slashStat, "ReaderBridge", "tick stats" })
insert(Command.Slash.Register("readergui"),  { slashGui,  "ReaderBridge", "toggle GUI panel" })

-- ============================================================================
-- Event attachment
-- ============================================================================
local function onLoad(addonId)
    if addonId ~= "ReaderBridge" then return end
    playerUnitID = safeLookup("player")
    targetUnitID = safeLookup("player.target")
    state.player.dirty = true
    state.target.dirty = true
    state.playerBuffs.dirty = true
    state.playerDebuffs.dirty = true
    cachedSlotA = buildEmptySlot()
    cachedSlotB = buildEmptySlot()
    publish()
    Command.Console.Display(
        "general", true,
        "<font color='#00CC88'>[ReaderBridge v" .. VERSION .. "]</font> v3 protocol loaded. /readerdump /readerstat /readergui",
        true
    )
end

local function safeAttach(event, handler, id)
    if event then
        local ok = pcall(function()
            Command.Event.Attach(event, handler, id)
        end)
        return ok
    end
    return false
end

Command.Event.Attach(Event.Addon.Load.End,         onLoad,              "RB.OnLoad")
Command.Event.Attach(Event.System.Update.End,      tick,                "RB.Tick")
safeAttach(Event.Unit.Availability.Full,           onAvailabilityFull,  "RB.AvailFull")
safeAttach(Event.Unit.Availability.None,           onAvailabilityNone,  "RB.AvailNone")

-- Player & target detail events
local playerEvts = {
    "Name", "Level", "Guild", "Calling",
    "Health", "HealthMax", "Mana", "ManaMax",
    "Energy", "EnergyMax", "Power", "Charge", "ChargeMax", "Combo",
    "Coord",
}
for _, k in ipairs(playerEvts) do
    if Event.Unit.Detail[k] then
        safeAttach(Event.Unit.Detail[k], onPlayerEvent, "RB.Player." .. k)
        safeAttach(Event.Unit.Detail[k], onTargetEvent, "RB.Target." .. k)
    end
end

-- Buff events (names may vary; all attach attempts are defensive)
if Event.Buff then
    safeAttach(Event.Buff.Add,    onBuffEvent, "RB.Buff.Add")
    safeAttach(Event.Buff.Remove, onBuffEvent, "RB.Buff.Remove")
    safeAttach(Event.Buff.Change, onBuffEvent, "RB.Buff.Change")
end

-- Combat events (names may vary; attach defensively)
if Event.Combat then
    safeAttach(Event.Combat.Damage, onCombatDamage, "RB.Combat.Damage")
    safeAttach(Event.Combat.Heal,   onCombatHeal,   "RB.Combat.Heal")
end
