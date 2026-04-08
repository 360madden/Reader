-- PlayerCoords.lua (v1.9)
local context = UI.CreateContext("PlayerCoordsContext")
local playerUnitID
local initDone = false
local needsLayout = true
local pad = 8
local gap = 2
local titleGap = 4

local frame = UI.CreateFrame("Frame", "PlayerCoordsFrame", context)
frame:SetWidth(10)
frame:SetHeight(10)
frame:SetPoint("CENTER", UIParent, "CENTER", 0, 0)
frame:SetBackgroundColor(0, 0, 0, 0.6)

local titleLabel = UI.CreateFrame("Text", "PlayerCoordsTitle", frame)
titleLabel:SetFontSize(15)
titleLabel:SetFontColor(0.9, 0.9, 0.9, 1)
titleLabel:SetText("PlayerCoords")

local labelX = UI.CreateFrame("Text", "PlayerCoordsLabelX", frame)
labelX:SetFontSize(12)
labelX:SetFontColor(1, 1, 0, 1)
labelX:SetText("X: --")

local labelY = UI.CreateFrame("Text", "PlayerCoordsLabelY", frame)
labelY:SetFontSize(12)
labelY:SetFontColor(1, 1, 0, 1)
labelY:SetText("Y: --")

local labelZ = UI.CreateFrame("Text", "PlayerCoordsLabelZ", frame)
labelZ:SetFontSize(12)
labelZ:SetFontColor(1, 1, 0, 1)
labelZ:SetText("Z: --")

table.insert(Event.System.Update.End, { function()
    if not needsLayout then return end
    needsLayout = false

    local tw, th = titleLabel:GetWidth(), titleLabel:GetHeight()
    local w1, h1 = labelX:GetWidth(), labelX:GetHeight()
    local w2 = labelY:GetWidth()
    local w3 = labelZ:GetWidth()
    local lh = h1

    local fw = math.max(tw, w1, w2, w3) + pad * 2
    local fh = th + titleGap + lh * 3 + gap * 2 + pad * 2

    if not initDone then
        local cx = frame:GetLeft() + frame:GetWidth() / 2
        local cy = frame:GetTop() + frame:GetHeight() / 2
        frame:ClearAll()
        frame:SetPoint("TOPLEFT", UIParent, "TOPLEFT", cx - fw / 2, cy - fh / 2)
        initDone = true
    end

    frame:SetWidth(fw)
    frame:SetHeight(fh)

    titleLabel:ClearAll()
    titleLabel:SetPoint("TOPLEFT", frame, "TOPLEFT", (fw - tw) / 2, pad)

    labelX:ClearAll()
    labelX:SetPoint("TOPLEFT", frame, "TOPLEFT", (fw - w1) / 2, pad + th + titleGap)

    labelY:ClearAll()
    labelY:SetPoint("TOPLEFT", frame, "TOPLEFT", (fw - w2) / 2, pad + th + titleGap + lh + gap)

    labelZ:ClearAll()
    labelZ:SetPoint("TOPLEFT", frame, "TOPLEFT", (fw - w3) / 2, pad + th + titleGap + lh * 2 + gap * 2)
end, "PlayerCoords", "Layout" })

frame:EventAttach(Event.UI.Input.Mouse.Left.Down, function(self)
    local m = Inspect.Mouse()
    self.drag = true
    self.ox = m.x - frame:GetLeft()
    self.oy = m.y - frame:GetTop()
end, "D")

frame:EventAttach(Event.UI.Input.Mouse.Left.Up, function(self)
    self.drag = false
end, "U")

frame:EventAttach(Event.UI.Input.Mouse.Cursor.Move, function(self)
    if not self.drag then return end
    local m = Inspect.Mouse()
    frame:SetPoint("TOPLEFT", UIParent, "TOPLEFT", m.x - self.ox, m.y - self.oy)
end, "M")

local function UpdateCoords()
    local detail = Inspect.Unit.Detail("player")
    if detail and detail.coordX and detail.coordY and detail.coordZ then
        labelX:SetText(string.format("X: %.2f", detail.coordX))
        labelY:SetText(string.format("Y: %.2f", detail.coordY))
        labelZ:SetText(string.format("Z: %.2f", detail.coordZ))
    else
        labelX:SetText("X: --")
        labelY:SetText("Y: --")
        labelZ:SetText("Z: --")
    end
    needsLayout = true
end

table.insert(Event.Addon.Load.End, { function(addonId)
    if addonId ~= "PlayerCoords" then return end
    playerUnitID = Inspect.Unit.Lookup("player")
    Command.Console.Display("general", false, "PlayerCoords loaded.", false)
    UpdateCoords()
end, "PlayerCoords", "OnLoad" })

table.insert(Event.Unit.Availability.Full, { function(units)
    if playerUnitID and units[playerUnitID] then
        UpdateCoords()
    end
end, "PlayerCoords", "OnAvail" })

table.insert(Event.Unit.Detail.Coord, { function(units)
    if playerUnitID and units[playerUnitID] then
        UpdateCoords()
    end
end, "PlayerCoords", "OnCoord" })

--[[
================================================================================
PLAYERCOORDS ADDON — DETAILED DOCUMENTATION
================================================================================

OVERVIEW
--------
PlayerCoords is a lightweight RIFT addon that displays the player's current
world coordinates (X, Y, Z) in a compact, draggable, auto-sizing frame. It is
designed to be minimal in memory footprint and CPU usage, firing layout
recalculations only when coordinate data actually changes, and deferring all
frame measurement to after the first render cycle so that text dimensions are
accurate.


ARCHITECTURE DECISIONS
-----------------------
The addon uses a single Lua file with no external dependencies. All state is
held in local upvalues rather than globals, preventing namespace pollution and
reducing the risk of conflicts with other addons. The frame hierarchy is flat:
one background Frame parent, four Text children. No tables, no metatables, no
OOP — this is intentional minimalism.

A title text frame (`titleLabel`) is included above the coordinate readout so
the addon is immediately identifiable to the player. This is useful because
similar coordinate addons may later be created for other units such as target,
focus, or mouseover. The title is cosmetic only and does not affect the data
path.


FRAME INITIALISATION
---------------------
The background frame is initially created at 10×10 pixels. This is deliberate:
at creation time, the Text children have not yet been rendered by the engine,
so their GetWidth() and GetHeight() calls would return zero or garbage. By
deferring all size calculations to Event.System.Update.End (which fires after
the first render pass), we guarantee that label dimensions are real and
accurate before we use them to size the frame.

The frame is initially anchored to CENTER of UIParent. However, CENTER anchors
cannot be repositioned by drag operations in the same way TOPLEFT anchors can,
because SetPoint with a new TOPLEFT while CENTER is active creates a conflicting
constraint. Therefore, on the very first layout pass, the CENTER anchor is
cleared (ClearAll()) and replaced with a TOPLEFT anchor calculated to
position the frame exactly where the CENTER anchor would have placed it. From
that point on, all drag and layout operations use TOPLEFT exclusively.


EVENT.SYSTEM.UPDATE.END — LAYOUT HANDLER
-----------------------------------------
This event fires once per render frame, after all frame updates are complete.
Our handler is registered permanently (it never removes itself) but is gated
by the boolean flag `needsLayout`. When needsLayout is false, the handler
returns immediately at negligible cost. When needsLayout is true (set by
UpdateCoords after new data arrives), the handler runs the full layout pass:

  1. needsLayout is set to false FIRST, before any work is done. This prevents
     re-entrant or redundant execution if any downstream call somehow triggers
     another event within the same frame.

  2. Label dimensions are measured via GetWidth() and GetHeight(). These are
     accurate post-render. The title label is measured the same way.

  3. Frame width is calculated as the widest text element plus padding on both
     sides. This now includes the title width as well as coordinate label widths.

  4. Frame height is calculated as title height, one title-to-body gap, three
     coordinate row heights, two inter-label gaps, plus top and bottom padding.

  5. Each text element is re-anchored to a TOPLEFT offset from the frame that
     centers it horizontally: offset = (frameWidth - textWidth) / 2. This
     produces true per-label horizontal centering regardless of varying text
     widths.

  6. The initDone flag ensures the CENTER-to-TOPLEFT conversion only happens
     once. On subsequent layout passes, the frame simply resizes and repositions
     its labels without moving its screen position.


DRAG IMPLEMENTATION
--------------------
Drag state is stored directly on the frame's self table (self.drag, self.ox,
self.oy) rather than in separate upvalue locals. This mirrors the pattern used
in the verified HelloWorld reference addon and avoids the need for additional
file-scope variables.

On LeftDown: the current mouse position is captured via Inspect.Mouse(), and
the offset from the mouse to the frame's top-left corner is stored. This offset
remains constant throughout the drag, so that the frame moves with the mouse
without snapping to the cursor position.

On Cursor.Move: if dragging, a new TOPLEFT anchor is set directly. Critically,
ClearAll() is NOT called here. ClearAll() would destroy the anchor points of
all child labels, causing them to detach and disappear. Instead, SetPoint with
"TOPLEFT" simply overwrites the existing TOPLEFT anchor in place, which is safe
and correct.

On LeftUp: drag state is cleared.


COORDINATE UPDATE — UPDATECOORDS()
------------------------------------
This function calls Inspect.Unit.Detail("player") directly rather than caching
a unit detail table. This is correct because coordinates change continuously
and the detail table must be re-fetched on every update. The playerUnitID is
cached (fetched once in OnLoad) solely for use as a key in the event handler
unit tables — it is never passed to Inspect.Unit.Detail, which accepts the
"player" specifier string directly and always returns current data.

All three coordinates (coordX, coordY, coordZ) are nil-checked explicitly
before formatting. This guards against the brief window at login or zone
transition where the unit detail table exists but coordinate fields have not
yet been populated by the server.

If any coordinate is nil, all three labels are set to "--" to avoid displaying
a partially populated or misleading readout.

After updating labels, needsLayout is set to true so that the layout handler
will resize and re-center the frame on the next render frame. This is necessary
because coordinate values vary in string length (e.g. "X: 9.01" vs
"X: 12345.67"), so the frame width and label positions must be recalculated
after every update.


EVENT REGISTRATION
-------------------
Three events drive the addon:

  Event.Addon.Load.End — fires when each addon finishes loading. We filter for
  our own addonId ("PlayerCoords") to avoid acting on other addons' load events.
  On our load: playerUnitID is looked up, the load confirmation message is
  displayed in the general chat channel, and an initial UpdateCoords() is
  attempted. This initial call may return "--" values if the player unit is not
  yet fully available, which is why the next two events exist.

  Event.Unit.Availability.Full — fires when a unit becomes fully available for
  inspection. We check whether the player's unitID is in the provided units
  table. If so, we call UpdateCoords(). This event reliably fires after login
  and after zone transitions, ensuring coordinates populate without requiring
  the player to move.

  Event.Unit.Detail.Coord — fires whenever one or more units' coordinates
  change. The callback receives a single `units` table whose keys are unitIDs
  that have changed. We check for our cached playerUnitID and call
  UpdateCoords() if the player is in the set. This is the primary real-time
  update path during normal gameplay.


CHAT OUTPUT
------------
Command.Console.Display("general", false, "message", false) is used for the
load confirmation message. The first argument specifies the channel
("general" routes to the General tab). The second and fourth arguments are
flags controlling display behavior.


FLAGS AND CONSTANTS
--------------------
  pad (8)      — pixel padding inside the frame on all sides. Controls the gap
                 between the frame edge and the nearest text edge.

  gap (2)      — pixel gap between consecutive coordinate labels vertically.

  titleGap (4) — pixel gap between the title and the first coordinate row.

  initDone     — boolean, false until the first layout pass converts the CENTER
                 anchor to TOPLEFT. Prevents repeated anchor conversion.

  needsLayout  — boolean, initialises as true so that the very first render
                 frame triggers a layout pass before any coordinate data has
                 arrived. Subsequently set to true by UpdateCoords whenever
                 label text changes, and back to false at the start of each
                 layout pass. Gating the Update.End handler on this flag means
                 layout work only occurs when genuinely needed, not every frame.

================================================================================
]]