namespace Reader.Models;

/// <summary>
/// A single group/raid member. Excludes the local player.
/// </summary>
/// <param name="UnitId">RIFT unit id.</param>
/// <param name="Name">Character name.</param>
/// <param name="Level">Character level.</param>
/// <param name="Calling">Warrior, Rogue, Cleric, Mage, Primalist.</param>
/// <param name="Role">Tank, DPS, Heal, Support.</param>
/// <param name="HpPercent">Current HP as a percentage (0-100).</param>
/// <param name="ResourcePercent">Current resource as a percentage (0-100).</param>
/// <param name="IsOnline">Whether the member is currently connected.</param>
/// <param name="IsDead">Whether the member is dead.</param>
/// <param name="ZoneName">Current zone of the member.</param>
public sealed record GroupMember(
    string UnitId,
    string Name,
    int? Level,
    string? Calling,
    string? Role,
    int? HpPercent,
    int? ResourcePercent,
    bool IsOnline,
    bool IsDead,
    string? ZoneName);
