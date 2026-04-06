namespace Reader.Models;

public sealed record TargetInfo(
    string? Name,
    int? Level,
    int? HpPercent,
    string? Relation);
