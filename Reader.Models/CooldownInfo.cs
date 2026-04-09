namespace Reader.Models;

/// <summary>
/// A single ability or spell cooldown entry.
/// </summary>
/// <param name="AbilityId">Ability identifier (RIFT ability id).</param>
/// <param name="Name">Human-readable ability name.</param>
/// <param name="RemainMs">Milliseconds remaining on cooldown (0 = ready).</param>
/// <param name="DurationMs">Total cooldown duration in milliseconds.</param>
/// <param name="ResourceCost">Resource cost to cast (0 if none).</param>
/// <param name="ResourceKind">Resource kind consumed (mana, power, energy, charge, combo).</param>
public sealed record CooldownInfo(
    long AbilityId,
    string Name,
    int RemainMs,
    int DurationMs,
    int ResourceCost,
    string? ResourceKind);
