namespace Reader.Models;

public sealed record ReaderSnapshot(
    ReaderPayloadVersion PayloadVersion,
    PlayerIdentity Player,
    PlayerStats Stats,
    PlayerPosition Position,
    TargetInfo? Target,
    DateTimeOffset Timestamp,
    ulong Seq = 0,
    long FrameTimeMs = 0,
    ReaderFlags Flags = ReaderFlags.None,
    IReadOnlyList<BuffInfo>? PlayerBuffs = null,
    IReadOnlyList<BuffInfo>? PlayerDebuffs = null,
    IReadOnlyList<BuffInfo>? TargetBuffs = null,
    IReadOnlyList<BuffInfo>? TargetDebuffs = null,
    IReadOnlyList<CombatEvent>? CombatEvents = null,
    CombatStats? Combat = null,
    ZoneInfo? Zone = null);
