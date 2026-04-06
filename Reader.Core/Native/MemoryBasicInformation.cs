using System.Runtime.InteropServices;

namespace Reader.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public nuint BaseAddress;
    public nuint AllocationBase;
    public uint  AllocationProtect;
    public ushort PartitionId;
    public nuint RegionSize;
    public uint  State;
    public uint  Protect;
    public uint  Type;
}
