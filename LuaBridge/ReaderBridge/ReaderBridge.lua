-- ReaderBridge.lua
-- Gathers player data via the RIFT addon API and writes it as a fixed-format
-- marker string into a global variable every 0.2s.
-- The C# Reader process scans rift.exe memory for this string.
--
-- Marker format (16 pipe-delimited fields):
-- ##READER_DATA##|name|level|calling|guild|hp|hpMax|resourceKind|resource|resourceMax|x|y|z|targetName|targetLevel|targetHpPct|targetRelation|##END_READER##

ReaderBridge = {}
ReaderBridge_Data = ""

local FRAMES_PER_UPDATE = 12  -- ~0.2s at 60fps
local frameCount = 0

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
    elseif detail.power ~= nil then
        return "power", detail.power, detail.powerMax or 100
    elseif detail.chargeMax and detail.chargeMax > 0 then
        return "charge", detail.charge or 0, detail.chargeMax
    elseif detail.combo ~= nil then
        return "combo", detail.combo, detail.comboMax or 0
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

local function OnUpdate(h)
    frameCount = frameCount + 1
    if frameCount < FRAMES_PER_UPDATE then return end
    frameCount = 0
    Refresh()
end

local VERSION = "0.1.0"

-- Debug dump window
local dumpCtx = UI.CreateContext("ReaderBridgeDump")
local dumpWin = UI.CreateFrame("Frame", "RBDumpWin", dumpCtx)
dumpWin:SetPoint("CENTER", UIParent, "CENTER")
dumpWin:SetWidth(500)
dumpWin:SetHeight(350)
dumpWin:SetBackgroundColor(0, 0, 0, 0.9)
dumpWin:SetVisible(false)

local dumpBar = UI.CreateFrame("Frame", "RBDumpBar", dumpWin)
dumpBar:SetPoint("TOPLEFT", dumpWin, "TOPLEFT", 0, 0)
dumpBar:SetPoint("TOPRIGHT", dumpWin, "TOPRIGHT", 0, 0)
dumpBar:SetHeight(20)
dumpBar:SetBackgroundColor(0.2, 0.2, 0.2, 1)

local dumpTitle = UI.CreateFrame("Text", "RBDumpTitle", dumpBar)
dumpTitle:SetPoint("CENTER", dumpBar, "CENTER")
dumpTitle:SetText("ReaderBridge - Player Detail Dump")

local dumpTf = UI.CreateFrame("RiftTextfield", "RBDumpTF", dumpWin)
dumpTf:SetPoint("TOPLEFT", dumpWin, "TOPLEFT", 5, 25)
dumpTf:SetPoint("BOTTOMRIGHT", dumpWin, "BOTTOMRIGHT", -5, -5)

-- Drag support
dumpBar:EventAttach(Event.UI.Input.Mouse.Left.Down, function(self)
    local m = Inspect.Mouse()
    self.drag = true
    self.ox = m.x - dumpWin:GetLeft()
    self.oy = m.y - dumpWin:GetTop()
end, "D")
dumpBar:EventAttach(Event.UI.Input.Mouse.Cursor.Move, function(self)
    if not self.drag then return end
    local m = Inspect.Mouse()
    dumpWin:SetPoint("TOPLEFT", UIParent, "TOPLEFT", m.x - self.ox, m.y - self.oy)
end, "M")
dumpBar:EventAttach(Event.UI.Input.Mouse.Left.Up, function(self)
    self.drag = false
end, "U")

local function DumpPlayerFields()
    local player = SafeUnitDetail("player")
    if not player then return end

    local keys = {}
    for k, _ in pairs(player) do keys[#keys + 1] = k end
    table.sort(keys)

    local lines = {}
    for _, k in ipairs(keys) do
        lines[#lines + 1] = k .. "=" .. tostring(player[k])
    end
    dumpTf:SetText(table.concat(lines, "\n"))
    dumpWin:SetVisible(true)
end

table.insert(Command.Slash.Register("readerdump"), {DumpPlayerFields, "ReaderBridge", "dump player fields"})

local function OnLoad()
    Command.Console.Display("general", true, "<font color='#00CC88'>[ReaderBridge v" .. VERSION .. "]</font> Loaded. Type /readerdump to inspect fields.", true)
    Refresh()
end

-- Attach events
Command.Event.Attach(Event.System.Update.Begin, OnUpdate, "ReaderBridge.OnUpdate")
Command.Event.Attach(Event.Addon.Load.End,      OnLoad,   "ReaderBridge.OnLoad")
