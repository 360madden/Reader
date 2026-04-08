namespace Reader.Models;

public sealed record BuffInfo(
    int Id,
    string Name,
    int Stacks,
    int RemainMs,
    bool CasterIsSelf);
