namespace Reader.Models;

public sealed record CombatStats(
    double Dps1s,
    double Dps5s,
    double Hps1s,
    double Hps5s,
    double Incoming1s,
    double Incoming5s);
