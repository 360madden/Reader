using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reader.Core;
using Reader.Models;

bool jsonMode = args.Contains("--json");
string[] filteredArgs = args.Where(a => a != "--json").ToArray();
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
        Console.WriteLine("ReaderBridge marker not found. Is the addon installed and RIFT UI loaded?");
        return;
    }

    Output(snap);
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
            Console.WriteLine(jsonMode ? "{}" : "Waiting for ReaderBridge marker...");
        else
            Output(snap);

        Thread.Sleep(intervalMs);
    }
}

void RunSmoke()
{
    if (!jsonMode) Console.WriteLine("Running smoke test with synthetic marker...");

    const string marker =
        "##READER_DATA##|Arthok|70|Mage|SomeGuild|12500|15000|mana|8900|10000|1234.56|789.01|-45.23|Dragnoth|72|55|hostile|##END_READER##";

    byte[] buf = Encoding.UTF8.GetBytes(marker);
    var snap = MarkerParser.ParseFromBuffer(buf);

    if (snap is null)
    {
        Console.WriteLine("FAIL: parser returned null.");
        return;
    }

    if (!jsonMode) Console.WriteLine("PASS");
    Output(snap);
}

void InstallAddon()
{
    string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string dest = Path.Combine(docs, "RIFT", "Interface", "Addons", "ReaderBridge");

    // Source is relative to the executable: ../../../../LuaBridge/ReaderBridge
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
        player = new
        {
            name     = s.Player.Name,
            level    = s.Player.Level,
            calling  = s.Player.Calling,
            guild    = s.Player.Guild,
        },
        hp = new
        {
            current = s.Stats.Hp,
            max     = s.Stats.HpMax,
        },
        resource = new
        {
            kind    = s.Stats.ResourceKind,
            current = s.Stats.Resource,
            max     = s.Stats.ResourceMax,
        },
        position = new
        {
            x = s.Position.X,
            y = s.Position.Y,
            z = s.Position.Z,
        },
        target = s.Target is null ? null : new
        {
            name     = s.Target.Name,
            level    = s.Target.Level,
            hpPct    = s.Target.HpPercent,
            relation = s.Target.Relation,
        },
    };
    Console.WriteLine(JsonSerializer.Serialize(obj, jsonOptions));
}

void PrintSnapshot(ReaderSnapshot s)
{
    Console.WriteLine($"[{s.Timestamp:HH:mm:ss.fff}]");
    Console.WriteLine($"  Player   : {s.Player.Name} (Lvl {s.Player.Level}) {s.Player.Calling} | Guild: {s.Player.Guild ?? "-"}");
    Console.WriteLine($"  HP       : {s.Stats.Hp} / {s.Stats.HpMax}");
    Console.WriteLine($"  Resource : {s.Stats.ResourceKind} {s.Stats.Resource} / {s.Stats.ResourceMax}");
    Console.WriteLine($"  Position : X={s.Position.X:F2} Y={s.Position.Y:F2} Z={s.Position.Z:F2}");

    if (s.Target is not null)
        Console.WriteLine($"  Target   : {s.Target.Name} (Lvl {s.Target.Level}) HP={s.Target.HpPercent}% [{s.Target.Relation}]");
    else
        Console.WriteLine($"  Target   : (none)");
}

void PrintHelp()
{
    Console.WriteLine("Reader - RIFT Memory Reader");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  Reader.Cli once [--json]              Single read and print");
    Console.WriteLine("  Reader.Cli watch [intervalMs] [--json] Continuous watch (default 500ms)");
    Console.WriteLine("  Reader.Cli smoke [--json]             Parse synthetic test data (no RIFT needed)");
    Console.WriteLine("  Reader.Cli install-addon              Copy ReaderBridge addon to RIFT addons folder");
}
