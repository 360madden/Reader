namespace Reader.Models;

public sealed record PlayerStats(
    int? Hp,
    int? HpMax,
    int? HpPercent,
    string? ResourceKind,
    int? Resource,
    int? ResourceMax,
    int? ResourcePercent);
