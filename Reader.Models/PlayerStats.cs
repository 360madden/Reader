namespace Reader.Models;

public sealed record PlayerStats(
    int? Hp,
    int? HpMax,
    string? ResourceKind,
    int? Resource,
    int? ResourceMax);
