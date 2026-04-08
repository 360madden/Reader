# Reader

A real-time memory reader for the [RIFT MMO](https://www.playrift.com/) game client, built with C# .NET 10.

Reader uses a **hybrid architecture**: a Lua addon inside RIFT gathers comprehensive player state via the official addon API and publishes it as a fixed-layout binary-text payload into memory. An external C# application scans and parses that payload with zero fragile memory offsets—no reverse engineering, no patch maintenance.

## Example Output

```
Player   : Arthok (Lvl 70) Mage | Guild: Pipes|And;Equals=InGuild
HP       : 12500 / 15000 (83%)
Resource : mana 8900 / 10000 (89%)
Position : X=1234.56 Y=789.01 Z=-45.23
Target   : Dragnoth (Lvl 72) HP=55% [hostile]
Buffs    : 4
Debuffs  : 2
Zone     : Mathosia (ID 38)
Combat   : DPS 2150 (1s avg)  |  Incoming 350 (1s avg)
```

## How It Works

```
RIFT (rift.exe)                              C# Reader
+----------------------------------+         +-----------------------------+
| ReaderBridge.lua addon           |         | Reader.Cli                  |
|  - Inspect.Unit.Detail() (live)  |         |   |                         |
|  - Event.System.Update.Begin     |  v3     |   v                         |
|    (50ms tick, dirty-flag flush) | payload | MemoryScanner (3-tier)      |
|  - Encodes fixed 8392-byte v3    | ------> |   1. Stable-address fast    |
|    layout with CRC32 + double    | memory  |   2. ±2 MB small window     |
|    buffering (A/B slots)         | scan    |   3. Full VirtualQueryEx    |
|  - 9 sections: player, target,   |         |                             |
|    buffs/debuffs, combat, zone,  |         |   v                         |
|    stats                         |         | MarkerParser (v3 fixed)     |
+----------------------------------+         |   - Fixed offset reads      |
                                            |   - CRC32 + torn-read       |
                                            |     guards                  |
                                            |                             |
                                            |   v                         |
                                            | ReaderSnapshot (model)      |
                                            +-----------------------------+
```

### The v3 Protocol

**v3 is a fixed-layout, integrity-guarded binary-text protocol:**

- **Layout (always exactly 8392 bytes):**
  ```
  [ MAGIC 8 ][ CONTROL 32 ][ SLOT_A 4176 ][ SLOT_B 4176 ]
  ```
  - `MAGIC`: 8-byte sentinel (`##RBR3##`)
  - `CONTROL`: Active slot selector (A or B) + sequence number for torn-read detection
  - `SLOT_A` / `SLOT_B`: Double-buffered payloads. Lua writes to inactive slot, then flips CONTROL.active.

- **Each slot contains (4176 bytes):**
  ```
  [ HEADER 64 ][ BODY 4096 ][ CRC32 8 ][ SLOT_END 8 ]
  ```
  - Fixed 64-byte header with zero-padded hex fields (section mask, sequence, flags)
  - Body: length-prefixed key=value pairs for 9 sections (see below)
  - CRC32: IEEE 802.3 checksum of header + body
  - SLOT_END: Fixed sentinel (`==RBRG==`)

- **Integrity guards (all three must pass):**
  1. `control.seq == slot.seq` — detects torn reads mid-swap
  2. `CRC32(header + body[0..len])` — detects corruption
  3. `SLOT_END sentinel at fixed offset` — detects length/structure damage

- **Why fixed length?** Lua's string allocator frequently reuses the same 8392-byte heap slot across publishes. Scanner's cached-address fast path hits ~95% of the time with zero rescan cost.

### The 9 Sections

| Tag | Name | Fields | Source |
|-----|------|--------|--------|
| P | Player | name, level, calling, guild | `Inspect.Unit.Detail("player")` |
| T | Target | name, level, hpPct, relation | `Inspect.Unit.Detail(targetId)` |
| S | Stats | hp, hpMax, hpPct, resourceKind, resource, resourceMax, resourcePct | health/resource APIs |
| Z | Zone | zoneId, zoneName | `Inspect.Zone.Current()` |
| B | Player Buffs | list of (id, name, stacks, remainMs, selfApplied) | `Inspect.Buff.List("player")` |
| D | Player Debuffs | list of (id, name, stacks, remainMs, selfApplied) | `Inspect.Buff.List("player", true)` |
| b | Target Buffs | list (same structure) | `Inspect.Buff.List(targetId)` |
| d | Target Debuffs | list (same structure) | `Inspect.Buff.List(targetId, true)` |
| C | Combat Events | rolling ring buffer: damage, healing, kills | `Event.Combat.Damage`, `Event.Combat.Heal`, etc. |

**Layout is fixed at 8392 bytes.** To add more data (cooldowns, talents, equipment, group/raid, currency), a phase 2 architecture must be designed — either extended slots, a separate parallel marker, or a TCP bridge.

## Why This Hybrid Approach?

**Direct memory offsets:**
- RIFT's Gamebryo 2.6 engine is undocumented and changes between patches.
- Maintaining offset tables is a constant burden with each patch cycle.

**Lua addon API:**
- Stable, well-documented, exposes all we need.
- Only limitation: no file I/O, `SavedVariables` flush on logout only.
- **Solution:** Scan the addon's in-memory payload from an external process.

**The payoff:**
- Game patches don't break the reader (offsets are inside RIFT's safe API).
- Data is authoritative from the addon itself.
- C# handles only scanning and parsing — logic stays in Lua where the data exists.

## Data Captured (v3)

See the 9 Sections table above. Summary:

- **Player identity:** name, level, calling, guild
- **Player stats:** HP (absolute + %), resource (absolute + %), resource kind
- **Position:** x, y, z
- **Target:** name, level, HP%, relation (hostile/neutral/friendly)
- **Buffs & Debuffs:** 4 lists (player buffs, player debuffs, target buffs, target debuffs) with stacks, remaining time, self-applied flag
- **Zone:** zone ID and name
- **Combat:** DPS (1s and 5s rolling averages), HPS, incoming damage (1s and 5s)

## Project Structure

```
Reader/
├── Reader.sln
├── Reader.Models/                # Pure data records
│   ├── ReaderSnapshot.cs
│   ├── BuffInfo.cs
│   ├── CombatEvent.cs
│   ├── CombatStats.cs
│   ├── ReaderFlags.cs
│   └── ...
├── Reader.Core/                  # Scanning, parsing, native interop
│   ├── MemoryScanner.cs          # 3-tier lookup (stable, window, full)
│   ├── MarkerParser.cs           # v3 fixed-offset parser
│   ├── Crc32.cs                  # IEEE 802.3 CRC-32 (Lua-compatible)
│   ├── V3Layout.cs               # Byte offsets, sentinel definitions
│   ├── V3Encoder.cs              # Builds v3 buffers (test/smoke)
│   ├── V3Section.cs              # Length-prefixed key=value walker
│   └── Native/                   # Win32 P/Invoke (OpenProcess, ReadProcessMemory, etc.)
├── Reader.Cli/                   # Console entry point
│   └── Program.cs                # Commands: once, watch, smoke, install-addon
├── Reader.Tests/                 # xUnit tests (16 passing)
│   ├── MarkerParserTests.cs      # v3 round-trip, CRC, torn-read, large buff lists
│   └── MemoryScannerTests.cs     # Buffer-level v3 fixtures
└── LuaBridge/
    ├── ReaderBridge/             # Main v3 emitter addon
    │   ├── RiftAddon.toc
    │   └── ReaderBridge.lua      # Pure-Lua CRC32, double-buffering, 9 sections
    ├── PlayerCoords/             # Standalone coords display (independent)
    │   ├── RiftAddon.toc
    │   └── Playercoords.lua
    └── ...
```

## Requirements

- Windows 10/11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- RIFT game client installed
- RIFT must be running (Reader attaches to `rift.exe` / `rift_x64.exe`)

## Setup

### 1. Build

```bash
dotnet build
```

### 2. Install the Lua Addon

```bash
dotnet run --project Reader.Cli -- install-addon
```

This copies the `LuaBridge/ReaderBridge/` folder to your RIFT addons folder:
`%USERPROFILE%\Documents\RIFT\Interface\Addons\ReaderBridge\`

Alternatively, copy the folder manually.

### 3. Launch RIFT

Log into a character. You should see a green `[ReaderBridge v0.3.0] Loaded` message in chat.

### 4. Run the Reader

```bash
# Single snapshot
dotnet run --project Reader.Cli -- once

# Continuous monitoring (default 500ms interval)
dotnet run --project Reader.Cli -- watch

# Custom interval (1 second)
dotnet run --project Reader.Cli -- watch 1000

# Test parsing without RIFT running (synthetic v3 payload)
dotnet run --project Reader.Cli -- smoke

# Same as smoke but JSON output
dotnet run --project Reader.Cli -- smoke --json
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `once` | Single memory scan, print snapshot, exit |
| `watch [ms]` | Continuous output every N milliseconds (default: 500) |
| `smoke [--json]` | Test v3 parser with synthetic buffer (no RIFT needed) |
| `install-addon` | Copy ReaderBridge addon to RIFT addons folder |
| `help` | Show available commands |

## In-Game Tools

The ReaderBridge addon exposes three slash commands:

- `/readerdump` — Draggable window showing raw `Inspect.Unit.Detail("player")` fields and current published v3 payload data
- `/readerstat` — Print in-game status (Lua side: dirty flags, event counts, cache state)
- `/readergui` — Display live status indicators (5 colored lights: Tick, Pub, Plr, Tgt, Cbt)

## Technical Details

### Memory Scanner

- **3-tier lookup strategy:**
  1. **Stable-address fast path:** Read 8392 bytes at cached address. If magic matches, parse and return. ~95% hit rate on live gameplay.
  2. **Small-window rescan:** If stable path fails, scan ±2 MB around cached address. RIFT's Lua GC tends to allocate nearby slots. Covers ~99% of misses.
  3. **Full scan:** If window rescan fails, walk all readable memory regions via `VirtualQueryEx` and search for the magic bytes.

- **Caching:** Address is cached after first full scan and re-validated on each read. If the cached read fails CRC, a rescan is triggered (usually finds it within the window).

- **Reduced allocations:** Uses `ArrayPool<byte>.Shared` for scan buffers. Final snapshot copy is unavoidable (span → new array for return).

### Marker Parser

- **Fixed-offset reads:** No tokenizing. Each field in the 64-byte header is read from a fixed offset (zero-padded hex).
- **Variable sections:** Body contains length-prefixed key=value pairs for each section (B, D, b, d, C, Z, P, T, S). Section presence is flagged in the header mask.
- **Integrity:** CRC32 checked against header + body. Torn-read detected via `control.seq == slot.seq`. Sentinel validates structure.
- **Fallback:** If v3 fails, code can fall back to v1/v2 parsers (implemented in MarkerParser for backward compat).

### Lua Emitter

- **Pure Lua CRC32:** Table-driven IEEE 802.3 implementation, byte-exact with C# `Crc32` class.
- **Double-buffering:** CONTROL block atomically flips `active` slot. Lua writes to inactive, reader always reads from active.
- **Event-driven:** Buffs, target detail, zone, combat events update immediately via addon events. Dirty flags coalesce on 50ms tick for publish.
- **Ring buffer:** Combat events stored in a rolling 100-entry ring, oldest discarded when full.
- **Throttle:** Publishes only when dirty, and only every 50ms (not per-event) to reduce churn.

## Testing

```bash
# Run all tests
dotnet test Reader.sln

# Run specific test file
dotnet test Reader.Tests/MarkerParserTests.cs

# Verbose output
dotnet test Reader.sln --verbosity=normal
```

**16 tests cover:**
- v3 round-trip (encode → parse → verify)
- CRC32 calculation and validation
- Torn-read detection
- Sentinel validation
- Large buff lists (50+ buffs)
- Embedded delimiters in guild names

## Tech Stack

- **C# / .NET 10** (`net10.0-windows`)
- **Lua 5.1** (RIFT addon API)
- **LibraryImport** (source-generated P/Invoke, no `DllImport`)
- **Span<T> / ArrayPool<byte>** for reduced-allocation memory operations
- **xUnit** for testing
- **No external NuGet dependencies**

## Performance

- **Stable fast path:** ~0.1 ms (just read + CRC check)
- **Small-window rescan:** ~10 ms (scan ±2 MB)
- **Full scan:** ~50–500 ms (depends on memory layout; RIFT heaps are typically <1 GB)
- **Addon overhead:** <1% CPU (event-driven, 50ms publish throttle)

## Known Limitations

1. **Fixed-size layout:** The 8392-byte v3 layout cannot be extended without a breaking redesign. Phase 2 must be architected for cooldowns, talents, equipment, group/raid, currency.
2. **No in-game configuration:** All settings are compile-time constants (tick rate, buffer size, CRC table).
3. **Single-character:** Reader captures only the currently logged-in character. Multi-boxing requires multiple Reader instances.

## Phase 2 Roadmap (Future)

v3 is stable and production-ready. Next planned additions:

1. **Cooldowns:** Spell/ability availability, resource costs, global cooldown
2. **Soul tree / talents:** Active spec, talent choices
3. **Equipment:** Equipped items, rarity, item level
4. **Group / raid:** Members, HP%, class, status
5. **Currency:** Gold, planar charges, other wallet currencies
6. **Status flags:** Mounted, swimming, flying, in combat, dead, casting

Implementing these requires a phase 2 architecture decision (extended slots, parallel marker, or TCP bridge). No rush — v3 is solid as-is.

## Troubleshooting

**Reader says "Addon not loaded":**
- Verify ReaderBridge appears in RIFT's addon list (Settings → Interface → Addons).
- Check `/readerdump` works in-game.
- Restart RIFT and re-run Reader.

**Reader says "CRC mismatch":**
- The payload is corrupted in flight or Lua didn't publish correctly.
- Usually transient. Try again.

**Reader says "Torn read":**
- Lua flipped the active slot mid-read.
- Scanner will rescan. Usually resolves within 50ms.

**Reader hangs on first scan:**
- Slow network or under load. First full scan can take several seconds.
- Subsequent reads use cached address and are instant.

## License

[MIT](LICENSE)
