namespace Reader.Models;

public sealed record CombatEvent(
    long T,
    string Src,
    string Dst,
    int AbilityId,
    string Type,
    int Amount,
    bool Crit,
    int Absorb);
