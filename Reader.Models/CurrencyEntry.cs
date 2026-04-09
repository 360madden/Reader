namespace Reader.Models;

/// <summary>
/// A single wallet/currency entry. Examples: platinum, gold, silver, copper,
/// planarite, infinity stones, void stones, marks of ascension, etc.
/// </summary>
/// <param name="Id">RIFT currency identifier.</param>
/// <param name="Name">Human-readable currency name.</param>
/// <param name="Amount">Current amount held.</param>
/// <param name="Max">Cap, if the currency is capped (null otherwise).</param>
public sealed record CurrencyEntry(
    string Id,
    string Name,
    long Amount,
    long? Max);
