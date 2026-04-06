-- ReaderBridge.lua
-- Gathers player data via the RIFT addon API and writes it as a fixed-format
-- marker string into a global variable every 0.2s.
-- The C# Reader process scans rift.exe memory for this string.
--
-- Marker format (16 pipe-delimited fields):
-- ##READER_DATA##|name|level|calling|guild|hp|hpMax|resourceKind|resource|resourceMax|x|y|z|targetName|targetLevel|targetHpPct|targetRelation|##END_READER##

ReaderBridge = {}
ReaderBridge_Data = ""

local UPDATE_INTERVAL = 0.2
local elapsed = 0

-- Safely call Inspect.Unit.Detail; returns nil on any error.
local function SafeUnitDetail(unitSpec)
    local ok, result = pcall(Inspect.Unit.Detail, unitSpec)
    if ok then return result end
    return nil
end

-- Determine the primary resource kind and its current/max values.
-- Priority: mana > energy > power > charge > combo
local function GetResource(detail)
    if detail.manaMax and detail.manaMax > 0 then
        return "mana", detail.mana or 0, detail.manaMax
    elseif detail.energyMax and detail.energyMax > 0 then
        return "energy", detail.energy or 0, detail.energyMax
    elseif detail.powerMax and detail.powerMax > 0 then
        return "power", detail.power or 0, detail.powerMax
    elseif detail.chargeMax and detail.chargeMax > 0 then
        return "charge", detail.charge or 0, detail.chargeMax
    elseif detail.comboMax and detail.comboMax > 0 then
        return "combo", detail.combo or 0, detail.comboMax
    end
    return "", 0, 0
end

-- Format a value as a string; nil/false becomes "".
local function F(v)
    if v == nil or v == false then return "" end
    return tostring(v)
end

-- Format a coordinate to 2 decimal places; nil becomes "".
local function FC(v)
    if v == nil then return "" end
    return string.format("%.2f", v)
end

-- Round a float to nearest integer (for HP percent).
local function Round(v)
    if v == nil then return nil end
    return math.floor(v * 100 + 0.5)
end

local function Refresh()
    local player = SafeUnitDetail("player")
    if not player then
        ReaderBridge_Data = ""
        return
    end

    local name    = F(player.name)
    local level   = F(player.level)
    local calling = F(player.calling)
    local guild   = F(player.guild)
    local hp      = F(player.health)
    local hpMax   = F(player.healthMax)
    local x       = FC(player.coordX)
    local y       = FC(player.coordY)
    local z       = FC(player.coordZ)

    local resourceKind, resource, resourceMax = GetResource(player)

    -- Target
    local targetName   = ""
    local targetLevel  = ""
    local targetHpPct  = ""
    local targetRel    = ""

    local targetId = nil
    local ok, lookup = pcall(Inspect.Unit.Lookup, "player.target")
    if ok and lookup then
        targetId = lookup
    end

    if targetId then
        local target = SafeUnitDetail(targetId)
        if target then
            targetName  = F(target.name)
            targetLevel = F(target.level)
            targetRel   = F(target.relation)
            if target.health and target.healthMax and target.healthMax > 0 then
                targetHpPct = F(Round(target.health / target.healthMax))
            end
        end
    end

    ReaderBridge_Data = "##READER_DATA##|"
        .. name         .. "|"
        .. level        .. "|"
        .. calling      .. "|"
        .. guild        .. "|"
        .. hp           .. "|"
        .. hpMax        .. "|"
        .. resourceKind .. "|"
        .. F(resource)  .. "|"
        .. F(resourceMax) .. "|"
        .. x            .. "|"
        .. y            .. "|"
        .. z            .. "|"
        .. targetName   .. "|"
        .. targetLevel  .. "|"
        .. targetHpPct  .. "|"
        .. targetRel
        .. "|##END_READER##"
end

local function OnUpdate(h, delta)
    elapsed = elapsed + delta
    if elapsed < UPDATE_INTERVAL then return end
    elapsed = 0
    Refresh()
end

local function OnLoad()
    Refresh()
end

-- Attach events
Command.Event.Attach(Event.System.Update.Begin, OnUpdate, "ReaderBridge.OnUpdate")
Command.Event.Attach(Event.Addon.Load.End,      OnLoad,   "ReaderBridge.OnLoad")
