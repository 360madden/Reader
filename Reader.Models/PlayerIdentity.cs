namespace Reader.Models;

public sealed record PlayerIdentity(
    string? Name,
    int? Level,
    string? Calling,
    string? Guild);
