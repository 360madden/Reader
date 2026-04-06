# Reader -- RIFT Memory Reader

## Overview

Reader extracts real-time player data from the RIFT MMO game client using a hybrid architecture:

1. **ReaderBridge** -- A small Lua addon running inside RIFT that gathers player data via the official addon API and formats it as a distinctive marker string stored in a Lua global variable
2. **Reader (C#)** -- A .NET 9 console application that attaches to the `rift.exe` process, scans memory for the marker string, and parses the structured data

This approach avoids fragile memory offset reverse-engineering. The Lua addon API is stable across game patches, and the marker string is controlled by us, making the scanner resilient.

## Why Not Pure Memory Reading?

RIFT runs on a heavily modified Gamebryo 2.6 engine (64-bit). Internal structures are undocumented and change between patches. Maintaining a table of offsets would be a constant maintenance burden.

RIFT's Lua addon API already exposes everything we need for the initial scope: player identity, health, resources, coordinates, and target info. The only limitation is that the addon API has no file I/O and SavedVariables only flush on logout -- so we bridge the gap by scanning for the addon's in-memory string from an external process.

## Data Flow

```
RIFT Game Client (rift.exe)
  |
  |  Lua VM
  v
ReaderBridge.lua
  |  Inspect.Unit.Detail("player") -> name, level, calling, guild, hp, resources, coords
  |  Inspect.Unit.Detail(target)   -> target name, level, hp%, relation
  |  Formats pipe-delimited marker string every 0.2s
  v
ReaderBridge_Data (Lua global variable -- UTF-8 string in process memory)
  = "##READER_DATA##|Arthok|70|Mage|...|##END_READER##"
  |
  |  ReadProcessMemory
  v
Reader.Core (C# .NET 9)
  |  MemoryScanner: VirtualQueryEx walk + pattern scan for marker bytes
  |  MarkerParser: split on "|", map fields to typed models
  v
ReaderSnapshot (C# record)
  |
  v
Console output (or future: overlay, API, etc.)
```

## Marker String Format

```
##READER_DATA##|{name}|{level}|{calling}|{guild}|{hp}|{hpMax}|{resourceKind}|{resource}|{resourceMax}|{x}|{y}|{z}|{targetName}|{targetLevel}|{targetHpPct}|{targetRelation}|##END_READER##
```

- 16 data fields between markers, pipe-delimited
- Numeric fields: plain decimal; coordinates: 2 decimal places
- Missing/nil values: empty string between pipes (`||`)
- The `##READER_DATA##` prefix is chosen to be unique in the process memory space

## Project Structure

```
Reader\
+-- Reader.sln
+-- Reader.Models\           # Pure data records (PlayerIdentity, PlayerStats, etc.)
+-- Reader.Core\             # Process attach, memory scan, marker parsing
|   +-- Native\              # Win32 P/Invoke (OpenProcess, ReadProcessMemory, etc.)
+-- Reader.Cli\              # Console app with watch/once/smoke/install-addon commands
+-- Reader.Tests\            # xUnit tests for parser and scanner
+-- LuaBridge\               # Lua addon source files
    +-- ReaderBridge\
        +-- RiftAddon.toc
        +-- ReaderBridge.lua
```

## Initial Scope (v0.1.0)

| Data | Source |
|------|--------|
| Player name, level, calling, guild | `Inspect.Unit.Detail("player")` |
| HP, Max HP | `Inspect.Unit.Detail("player").health/healthMax` |
| Primary resource (mana/energy/power), max | Best non-zero from mana/energy/power fields |
| Location (x, y, z) | `Inspect.Unit.Detail("player")` coord fields |
| Target name, level, HP%, relation | `Inspect.Unit.Detail(targetId)` |

## Tech Stack

- C# / .NET 9.0 (`net9.0-windows`)
- `LibraryImport` source-generated P/Invoke
- `Span<T>` for zero-allocation memory operations
- RIFT Lua addon API (`Inspect.*` namespace)
- xUnit for testing

## CLI Usage

```
Reader.Cli watch [intervalMs]   # Continuous output (default 500ms)
Reader.Cli once                 # Single read, print, exit
Reader.Cli smoke                # Test parsing with synthetic data (no RIFT needed)
Reader.Cli install-addon        # Copy ReaderBridge addon to RIFT addons folder
```

## Setup

1. Build the solution: `dotnet build`
2. Install the Lua addon: `Reader.Cli install-addon` (or manually copy `LuaBridge/ReaderBridge/` to `%USERPROFILE%\Documents\RIFT\Interface\Addons\ReaderBridge\`)
3. Launch RIFT and log in (the addon loads automatically)
4. Run: `Reader.Cli watch`
