using System.Buffers;
using Reader.Core.Native;
using Reader.Models;

namespace Reader.Core;

/// <summary>
/// Scans the RIFT process memory for the ReaderBridge marker string and parses it.
/// </summary>
public sealed class MemoryScanner
{
    // Largest contiguous read we'll attempt per region (4 MB)
    private const int MaxReadSize = 4 * 1024 * 1024;

    // Max bytes to read once the marker start address is known
    private const int MarkerReadSize = 4096;

    private readonly nint _handle;

    // Cached address of the last-found marker start (0 = not cached)
    private nuint _cachedAddress;

    private static ReadOnlySpan<byte> StartMarker => "##READER_DATA##|"u8;
    private static ReadOnlySpan<byte> EndMarker   => "##END_READER##"u8;

    public MemoryScanner(nint processHandle)
    {
        _handle = processHandle;
    }

    /// <summary>
    /// Reads the latest ReaderBridge snapshot from process memory.
    /// Returns null if the marker is not found or the addon is not active.
    /// </summary>
    public ReaderSnapshot? Read()
    {
        // Try cached address first
        if (_cachedAddress != 0)
        {
            byte[]? cached = ReadAt(_cachedAddress, MarkerReadSize);
            if (cached is not null)
            {
                var snap = MarkerParser.ParseFromBuffer(cached);
                if (snap is not null) return snap;
            }
            // Cache miss — do a full scan
            _cachedAddress = 0;
        }

        return FullScan();
    }

    private ReaderSnapshot? FullScan()
    {
        nuint address = 0;

        while (true)
        {
            nuint queryResult = Kernel32.VirtualQueryEx(
                _handle,
                address,
                out MemoryBasicInformation mbi,
                (nuint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryBasicInformation>());

            if (queryResult == 0) break; // end of address space

            nuint regionEnd = mbi.BaseAddress + mbi.RegionSize;

            if (IsReadable(mbi))
            {
                var result = ScanRegion(mbi.BaseAddress, (int)Math.Min(mbi.RegionSize, (nuint)MaxReadSize));
                if (result is not null) return result;
            }

            // Advance past this region
            if (regionEnd <= address) break; // overflow guard
            address = regionEnd;
        }

        return null;
    }

    private ReaderSnapshot? ScanRegion(nuint baseAddress, int size)
    {
        byte[]? buf = ReadAt(baseAddress, size);
        if (buf is null) return null;

        int idx = IndexOf(buf, StartMarker);
        if (idx < 0) return null;

        // Found the marker — store the address for next time
        _cachedAddress = baseAddress + (nuint)idx;

        return MarkerParser.ParseFromBuffer(buf.AsSpan(idx));
    }

    private unsafe byte[]? ReadAt(nuint address, int size)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            fixed (byte* ptr = buf)
            {
                bool ok = Kernel32.ReadProcessMemory(_handle, address, ptr, (nuint)size, out nuint bytesRead);
                if (!ok || bytesRead == 0) return null;

                // Return a correctly-sized copy
                byte[] result = new byte[(int)bytesRead];
                Buffer.BlockCopy(buf, 0, result, 0, result.Length);
                return result;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static bool IsReadable(in MemoryBasicInformation mbi)
    {
        if (mbi.State != Kernel32.MemCommit) return false;

        uint protect = mbi.Protect;
        if ((protect & Kernel32.PageNoAccess) != 0) return false;
        if ((protect & Kernel32.PageGuard) != 0) return false;

        return true;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        ReadOnlySpan<byte> span = haystack;
        return span.IndexOf(needle);
    }
}
