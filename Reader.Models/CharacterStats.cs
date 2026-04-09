namespace Reader.Models;

/// <summary>
/// Primary and secondary character attributes exposed by Inspect.Unit.Detail()
/// and Inspect.Item.* equipped-item stat aggregation.
/// </summary>
public sealed record CharacterStats(
    // Primary attributes
    int? Strength,
    int? Dexterity,
    int? Intelligence,
    int? Wisdom,
    int? Endurance,

    // Defensive
    int? Armor,
    int? DeflectChance,
    int? DodgeChance,
    int? ParryChance,
    int? ResistAir,
    int? ResistDeath,
    int? ResistEarth,
    int? ResistFire,
    int? ResistLife,
    int? ResistWater,

    // Offensive (secondary ratings)
    int? CritHit,
    int? Hit,
    int? AttackPower,
    int? SpellPower,

    // Tempo
    int? Physical,      // haste rating (physical)
    int? Spell);        // haste rating (spell)
