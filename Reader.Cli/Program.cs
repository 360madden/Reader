using System.Text.Json;
using System.Text.Json.Serialization;
using Reader.Core;
using Reader.Models;

bool jsonMode = args.Contains("--json");
bool showStats = args.Contains("--stats");
string[] filteredArgs = args.Where(a => a != "--json" && a != "--stats").ToArray();
string command = filteredArgs.Length > 0 ? filteredArgs[0].ToLowerInvariant() : "help";

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

switch (command)
{
    case "once":
        RunOnce();
        break;

    case "watch":
        int interval = filteredArgs.Length > 1 && int.TryParse(filteredArgs[1], out int ms) ? ms : 500;
        RunWatch(interval);
        break;

    case "smoke":
        RunSmoke();
        break;

    case "install-addon":
        InstallAddon();
        break;

    default:
        PrintHelp();
        break;
}

void RunOnce()
{
    using var attacher = ProcessAttacher.Attach();
    if (attacher is null)
    {
        Console.WriteLine("RIFT process not found. Is the game running?");
        return;
    }

    Console.WriteLine($"Attached to RIFT (PID {attacher.ProcessId})");
    var scanner = new MemoryScanner(attacher.Handle);
    var snap = scanner.Read();

    if (snap is null)
    {
        Console.WriteLine("ReaderBridge v3 marker not found. Is the addon installed and RIFT UI loaded?");
        return;
    }

    Output(snap);
    if (showStats) PrintStats(scanner.Stats);
}

void RunWatch(int intervalMs)
{
    using var attacher = ProcessAttacher.Attach();
    if (attacher is null)
    {
        Console.WriteLine("RIFT process not found. Is the game running?");
        return;
    }

    if (!jsonMode)
        Console.WriteLine($"Attached to RIFT (PID {attacher.ProcessId}). Watching every {intervalMs}ms. Ctrl+C to stop.");

    var scanner = new MemoryScanner(attacher.Handle);

    while (true)
    {
        if (!jsonMode) Console.Clear();
        var snap = scanner.Read();
        if (snap is null)
            Console.WriteLine(jsonMode ? "{}" : "Waiting for ReaderBridge v3 marker...");
        else
            Output(snap);

        if (showStats && !jsonMode) PrintStats(scanner.Stats);

        Thread.Sleep(intervalMs);
    }
}

void RunSmoke()
{
    if (!jsonMode) Console.WriteLine("Running smoke test with synthetic v3 buffer...");

    var encoder = new V3Encoder();
    var snapshot = new ReaderSnapshot(
        ReaderPayloadVersion.V3,
        new PlayerIdentity("Arthok", 70, "Mage", "SomeGuild"),
        new PlayerStats(12500, 15000, 83, "mana", 8900, 10000, 89),
        new PlayerPosition(1234.56f, 789.01f, -45.23f),
        new TargetInfo("Dragnoth", 72, 55, "hostile"),
        DateTimeOffset.UtcNow,
        Seq: 42,
        FrameTimeMs: 123_456,
        Flags: ReaderFlags.HasTarget | ReaderFlags.InCombat,
        Combat: new CombatStats(2150.5, 1980.2, 0, 0, 350.1, 280.4),
        Zone: new ZoneInfo(38, "Mathosia"));

    byte[] buf = encoder.Build(seq: 42, frameTimeMs: 123_456, flags: ReaderFlags.HasTarget | ReaderFlags.InCombat, activeSlot: 'A', snapshot);

    var parsed = MarkerParser.ParseFromBuffer(buf);
    if (parsed is null)
    {
        Console.WriteLine("FAIL: parser returned null.");
        return;
    }

    if (!jsonMode) Console.WriteLine("PASS");
    Output(parsed);
}

void InstallAddon()
{
    string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string dest = Path.Combine(docs, "RIFT", "Interface", "Addons", "ReaderBridge");

    string exe = AppContext.BaseDirectory;
    string src = Path.GetFullPath(Path.Combine(exe, "..", "..", "..", "..", "LuaBridge", "ReaderBridge"));

    if (!Directory.Exists(src))
    {
        Console.WriteLine($"Source addon folder not found: {src}");
        Console.WriteLine("Run this from the solution root or adjust the path.");
        return;
    }

    Directory.CreateDirectory(dest);

    foreach (string file in Directory.GetFiles(src))
    {
        string destFile = Path.Combine(dest, Path.GetFileName(file));
        File.Copy(file, destFile, overwrite: true);
        Console.WriteLine($"  Copied: {Path.GetFileName(file)}");
    }

    Console.WriteLine($"ReaderBridge installed to: {dest}");
    Console.WriteLine("Restart RIFT or /reloadui to activate.");
}

void Output(ReaderSnapshot s)
{
    if (jsonMode)
        PrintJson(s);
    else
        PrintSnapshot(s);
}

void PrintJson(ReaderSnapshot s)
{
    var obj = new
    {
        timestamp = s.Timestamp.ToString("o"),
        payloadVersion = (int)s.PayloadVersion,
        seq = s.Seq,
        frameTimeMs = s.FrameTimeMs,
        flags = s.Flags.ToString(),
        player = new
        {
            name = s.Player.Name,
            level = s.Player.Level,
            calling = s.Player.Calling,
            guild = s.Player.Guild,
        },
        hp = new { current = s.Stats.Hp, max = s.Stats.HpMax, pct = s.Stats.HpPercent },
        resource = new
        {
            kind = s.Stats.ResourceKind,
            current = s.Stats.Resource,
            max = s.Stats.ResourceMax,
            pct = s.Stats.ResourcePercent,
        },
        position = new { x = s.Position.X, y = s.Position.Y, z = s.Position.Z },
        target = s.Target is null ? null : new
        {
            name = s.Target.Name,
            level = s.Target.Level,
            hpPct = s.Target.HpPercent,
            relation = s.Target.Relation,
        },
        zone = s.Zone,
        combat = s.Combat,
        playerBuffs = s.PlayerBuffs,
        playerDebuffs = s.PlayerDebuffs,
        targetBuffs = s.TargetBuffs,
        targetDebuffs = s.TargetDebuffs,
        combatEvents = s.CombatEvents,
    };
    Console.WriteLine(JsonSerializer.Serialize(obj, jsonOptions));
}

void PrintSnapshot(ReaderSnapshot s)
{
    Console.WriteLine($"[{s.Timestamp:HH:mm:ss.fff}] [v{(int)s.PayloadVersion}] seq={s.Seq} t={s.FrameTimeMs}ms flags={s.Flags}");
    Console.WriteLine($"  Player   : {s.Player.Name} (Lvl {s.Player.Level}) {s.Player.Calling} | Guild: {s.Player.Guild ?? "-"}");
    Console.WriteLine($"  HP       : {FormatStat(s.Stats.Hp, s.Stats.HpMax, s.Stats.HpPercent)}");
    Console.WriteLine($"  Resource : {s.Stats.ResourceKind ?? "-"} {FormatStat(s.Stats.Resource, s.Stats.ResourceMax, s.Stats.ResourcePercent)}");
    Console.WriteLine($"  Position : X={FormatFloat(s.Position.X)} Y={FormatFloat(s.Position.Y)} Z={FormatFloat(s.Position.Z)}");

    if (s.Zone is not null)
        Console.WriteLine($"  Zone     : {s.Zone.Name} (id {s.Zone.Id})");

    if (s.Target is not null)
        Console.WriteLine($"  Target   : {s.Target.Name} (Lvl {s.Target.Level}) HP={FormatPercent(s.Target.HpPercent)} [{s.Target.Relation}]");
    else
        Console.WriteLine("  Target   : (none)");

    if (s.Combat is not null)
        Console.WriteLine($"  Combat   : DPS {s.Combat.Dps1s:F0}/{s.Combat.Dps5s:F0}  HPS {s.Combat.Hps1s:F0}/{s.Combat.Hps5s:F0}  IN {s.Combat.Incoming1s:F0}/{s.Combat.Incoming5s:F0}");

    if (s.PlayerBuffs is { Count: > 0 } pb)   Console.WriteLine($"  Buffs    : {pb.Count}");
    if (s.PlayerDebuffs is { Count: > 0 } pd) Console.WriteLine($"  Debuffs  : {pd.Count}");
    if (s.CombatEvents is { Count: > 0 } ce)  Console.WriteLine($"  Events   : {ce.Count} since last tick");

    Console.WriteLine();
}

void PrintStats(ScannerStats stats)
{
    Console.WriteLine($"  Scanner  : stable={stats.StableHits} window={stats.SmallWindowHits} full={stats.FullScanHits} crcFail={stats.CrcFailures}");
}

string FormatStat(int? current, int? max, int? percent)
{
    if (current is not null && max is not null && percent is not null)
        return $"{current} / {max} ({percent}%)";
    if (current is not null && max is not null)
        return $"{current} / {max}";
    if (percent is not null)
        return $"{percent}%";
    return "-";
}

string FormatPercent(int? percent) => percent is null ? "-" : $"{percent}%";
string FormatFloat(float? value)   => value is null ? "-" : value.Value.ToString("F2");

void PrintHelp()
{
    Console.WriteLine("Reader - RIFT Memory Reader (v3 protocol)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  Reader.Cli once [--json] [--stats]               Single read and print");
    Console.WriteLine("  Reader.Cli watch [intervalMs] [--json] [--stats] Continuous watch (default 500ms)");
    Console.WriteLine("  Reader.Cli smoke [--json]                        Encode + parse a synthetic v3 buffer");
    Console.WriteLine("  Reader.Cli install-addon                         Copy ReaderBridge addon to RIFT addons folder");
}
