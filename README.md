# Reader

A real-time memory reader for the [RIFT MMO](https://www.playrift.com/) game client, built with C# .NET 9.

Reader uses a **hybrid architecture**: a small Lua addon inside RIFT gathers player data via the official addon API and writes it as a scannable marker string into memory. An external C# application then reads that string from the game process — no fragile memory offsets, no reverse engineering required.

## Example Output

```
Player   : Atank (Lvl 43) warrior | Guild: The Regulators
HP       : 16041 / 16041
Resource : power 100 / 100
Position : X=7154.78 Y=871.77 Z=3053.22
Target   : (none)
```

## How It Works

```
RIFT (rift.exe)                          C# Reader
+--------------------------+             +------------------------+
| ReaderBridge.lua addon   |             | Reader.Cli             |
|  - Inspect.Unit.Detail() |             |   |                    |
|  - Formats marker string |  memory     |   v                    |
|  - Updates every ~0.2s   | ---------> | MemoryScanner          |
|  - Stores in Lua global  |  scan      |   |                    |
+--------------------------+             |   v                    |
                                         | MarkerParser           |
                                         |   |                    |
                                         |   v                    |
                                         | ReaderSnapshot (model) |
                                         +------------------------+
```

1. **ReaderBridge** (Lua addon) calls `Inspect.Unit.Detail("player")` every 12 frames (~0.2s) and formats the result as a pipe-delimited marker string stored in a global variable
2. **MemoryScanner** (C#) walks the RIFT process memory via `VirtualQueryEx`, finds the `##READER_DATA##` marker using byte pattern scanning, and caches the address for fast subsequent reads
3. **MarkerParser** splits the marker string on `|` and maps the 16 fields into typed C# records

### Why Not Pure Memory Reading?

RIFT runs on a heavily modified Gamebryo 2.6 engine (64-bit). Internal data structures are undocumented and change between patches. Maintaining a table of offsets would be a constant maintenance burden.

RIFT's Lua addon API is stable across patches and already exposes everything we need. The only limitation is that addons have no file I/O and `SavedVariables` only flush on logout — so we bridge the gap by scanning for the addon's in-memory string from an external process.

## Data Captured

| Field | Source |
|-------|--------|
| Player name, level, calling, guild | `Inspect.Unit.Detail("player")` |
| HP / Max HP | `.health` / `.healthMax` |
| Primary resource + max | Best non-zero from mana, energy, power, charge, combo |
| Location (x, y, z) | `.coordX`, `.coordY`, `.coordZ` |
| Target name, level, HP%, relation | `Inspect.Unit.Detail(targetId)` |

## Project Structure

```
Reader/
├── Reader.sln
├── Reader.Models/           # Pure data records (zero dependencies)
├── Reader.Core/             # Process attach, memory scan, marker parsing
│   └── Native/              # Win32 P/Invoke (OpenProcess, ReadProcessMemory, etc.)
├── Reader.Cli/              # Console entry point
├── Reader.Tests/            # xUnit tests
└── LuaBridge/
    └── ReaderBridge/        # RIFT Lua addon
        ├── RiftAddon.toc
        └── ReaderBridge.lua
```

## Requirements

- Windows 10/11
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
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

This copies the `ReaderBridge` addon to your RIFT addons folder:
`%USERPROFILE%\Documents\RIFT\Interface\Addons\ReaderBridge\`

Alternatively, copy the `LuaBridge/ReaderBridge/` folder there manually.

### 3. Launch RIFT

Log into a character. You should see a green `[ReaderBridge v0.1.0] Loaded` message in chat.

### 4. Run the Reader

```bash
# Single snapshot
dotnet run --project Reader.Cli -- once

# Continuous monitoring (default 500ms interval)
dotnet run --project Reader.Cli -- watch

# Custom interval (1 second)
dotnet run --project Reader.Cli -- watch 1000

# Test parsing without RIFT running
dotnet run --project Reader.Cli -- smoke
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `once` | Single memory scan, print snapshot, exit |
| `watch [ms]` | Continuous output every N milliseconds (default: 500) |
| `smoke` | Test marker parsing with synthetic data (no RIFT needed) |
| `install-addon` | Copy ReaderBridge addon to RIFT addons folder |
| `help` | Show available commands |

## Marker Format

The Lua addon writes a pipe-delimited string with 16 fields between start/end markers:

```
##READER_DATA##|name|level|calling|guild|hp|hpMax|resourceKind|resource|resourceMax|x|y|z|targetName|targetLevel|targetHpPct|targetRelation|##END_READER##
```

Missing values are represented as empty strings between pipes (`||`).

## In-Game Debug Tool

The addon includes a `/readerdump` slash command that opens a draggable window showing all raw fields from `Inspect.Unit.Detail("player")`. Useful for discovering available API fields.

## Technical Details

- **Scanner**: Walks memory regions via `VirtualQueryEx`, filters for committed readable pages, scans with `Span<byte>.IndexOf` for the marker bytes
- **Address caching**: After the first full scan, the marker address is cached and re-validated on each read — full re-scans only happen if the cache misses (e.g., after `/reloadui`)
- **False positive rejection**: The scanner iterates all marker occurrences per region (skipping the Lua source code literal) and validates parsed results (printable name, level 1–70, non-negative HP)
- **Zero-GC scanning**: Uses `ArrayPool<byte>.Shared` for scan buffers
- **P/Invoke**: Modern `LibraryImport` source-generated marshalling (no `DllImport`)

## Tech Stack

- C# / .NET 9.0 (`net9.0-windows`)
- `LibraryImport` (source-generated P/Invoke)
- `Span<T>` / `ArrayPool<byte>` for zero-allocation memory operations
- RIFT Lua addon API (`Inspect.*`, `Command.*`, `Event.*`)
- xUnit for testing
- No external NuGet dependencies

## License

[MIT](LICENSE)
