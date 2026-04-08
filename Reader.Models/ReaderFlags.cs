namespace Reader.Models;

[Flags]
public enum ReaderFlags : uint
{
    None                 = 0,
    InCombat             = 1u << 0,
    HasTarget            = 1u << 1,
    Grouped              = 1u << 2,
    Resting              = 1u << 3,
    Mounted              = 1u << 4,
    PartialAvailability  = 1u << 5,
    Truncated            = 1u << 6,
}
