using System.Runtime.InteropServices;

namespace Reader.Core.Native;

internal static partial class Kernel32
{
    internal const uint ProcessVmRead           = 0x0010;
    internal const uint ProcessQueryInformation = 0x0400;

    internal const uint MemCommit  = 0x1000;
    internal const uint PageNoAccess        = 0x01;
    internal const uint PageGuard           = 0x100;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadProcessMemory(
        nint hProcess,
        nuint lpBaseAddress,
        byte* lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQueryEx(
        nint hProcess,
        nuint lpAddress,
        out MemoryBasicInformation lpBuffer,
        nuint dwLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);
}
