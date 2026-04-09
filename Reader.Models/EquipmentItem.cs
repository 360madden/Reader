namespace Reader.Models;

/// <summary>
/// A single equipped item in a specific slot.
/// </summary>
/// <param name="Slot">Equipment slot name (Head, Shoulders, Chest, Hands, Legs, Feet, Wrist, Neck, Finger1, Finger2, Back, Seal, Trinket1, Trinket2, MainHand, SecondaryHand, Ranged).</param>
/// <param name="ItemId">RIFT item identifier, if known.</param>
/// <param name="Name">Human-readable item name.</param>
/// <param name="Rarity">Rarity tier (common, uncommon, rare, epic, relic, transcendent).</param>
/// <param name="ItemLevel">Item level (iLvl).</param>
/// <param name="Augments">Number of augments/runes applied.</param>
public sealed record EquipmentItem(
    string Slot,
    string? ItemId,
    string Name,
    string? Rarity,
    int? ItemLevel,
    int? Augments);
