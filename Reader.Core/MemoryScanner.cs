using System.Buffers;
using Reader.Core.Native;
using Reader.Models;

namespace Reader.Core;

/// <summary>
/// Scans the RIFT process memory for the v3 ReaderBridge payload.
///
/// The v3 payload is always exactly <see cref="V3Layout.TotalLen"/> bytes
/// (8392), starts with the 8-byte <see cref="V3Layout.Magic"/>, and ends with
/// the <see cref="V3Layout.SlotEnd"/> sentinel inside the active slot. Because
/// the byte length is fixed, the Lua string allocator frequently reuses the
/// same heap slot across publishes, so the cached address hits on most reads
/// with zero rescan cost.
///
/// Lookup strategy (in priority order):
///   1. Stable-address fast path: read <see cref="V3Layout.TotalLen"/> bytes at
///      the cached address. If the magic matches, parse and return.
///   2. Small-window rescan: read ±<see cref="RescanWindow"/> bytes around the
///      cached address. New strings tend to be allocated near old ones in
///      Lua's GC heap.
///   3. Full scan: enumerate readable regions via VirtualQueryEx and search
///      each for the magic.
/// </summary>
public sealed class MemoryScanner
{
    private const int RescanWindow = 2 * 1024 * 1024;     // ±2 MB
    private const int FullScanRegionMax = 4 * 1024 * 1024; // 4 MB chunks

    private readonly nint _handle;

    private nuint _cachedAddress;

    public ScannerStats Stats { get; } = new();

    public MemoryScanner(nint processHandle)
    {
        _handle = processHandle;
    }

    /// <summary>
    /// Reads the latest v3 snapshot. Returns null if the addon is not loaded
    /// or no valid payload was found.
    /// </summary>
    public ReaderSnapshot? Read()
    {
        // 1. Stable-address fast path
        if (_cachedAddress != 0)
        {
            byte[]? buf = ReadAt(_cachedAddress, V3Layout.TotalLen);
            if (buf is not null && buf.Length >= V3Layout.MagicLen
                && buf.AsSpan(0, V3Layout.MagicLen).SequenceEqual(V3Layout.Magic))
            {
                var snap = MarkerParser.ParseFromBuffer(buf);
                if (snap is not null)
                {
                    Stats.StableHits++;
                    return snap;
                }
                Stats.CrcFailures++;
            }

            // 2. Small-window rescan
            var windowSnap = SmallWindowRescan(_cachedAddress);
            if (windowSnap is not null)
            {
                Stats.SmallWindowHits++;
                return windowSnap;
            }

            _cachedAddress = 0;
        }

        // 3. Full scan
        var fullSnap = FullScan();
        if (fullSnap is not null)
            Stats.FullScanHits++;
        return fullSnap;
    }

    private ReaderSnapshot? SmallWindowRescan(nuint cached)
    {
        nuint windowStart = cached > (nuint)RescanWindow ? cached - (nuint)RescanWindow : 0;
        int windowSize = RescanWindow * 2 + V3Layout.TotalLen;

        byte[]? buf = ReadAt(windowStart, windowSize);
        if (buf is null) return null;

        return SearchAndParse(buf, windowStart);
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

            if (queryResult == 0) break;

            nuint regionEnd = mbi.BaseAddress + mbi.RegionSize;

            if (IsReadable(mbi))
            {
                int regionSize = (int)Math.Min(mbi.RegionSize, (nuint)FullScanRegionMax);
                byte[]? buf = ReadAt(mbi.BaseAddress, regionSize);
                if (buf is not null)
                {
                    var snap = SearchAndParse(buf, mbi.BaseAddress);
                    if (snap is not null) return snap;
                }
            }

            if (regionEnd <= address) break;
            address = regionEnd;
        }

        return null;
    }

    private ReaderSnapshot? SearchAndParse(ReadOnlySpan<byte> buf, nuint baseAddress)
    {
        var magic = V3Layout.Magic;
        int searchFrom = 0;
        while (searchFrom + V3Layout.TotalLen <= buf.Length)
        {
            int idx = buf[searchFrom..].IndexOf(magic);
            if (idx < 0) break;
            int abs = searchFrom + idx;

            if (abs + V3Layout.TotalLen <= buf.Length)
            {
                var candidate = buf.Slice(abs, V3Layout.TotalLen);

                // Cheap fingerprint reject: SLOT_END must be at one of the two known offsets.
                bool slotAEnd = candidate.Slice(V3Layout.SlotAOff + V3Layout.SlotEndOff, V3Layout.SlotEndLen)
                                         .SequenceEqual(V3Layout.SlotEnd);
                bool slotBEnd = candidate.Slice(V3Layout.SlotBOff + V3Layout.SlotEndOff, V3Layout.SlotEndLen)
                                         .SequenceEqual(V3Layout.SlotEnd);
                if (slotAEnd && slotBEnd)
                {
                    var snap = MarkerParser.ParseFromBuffer(candidate);
                    if (snap is not null)
                    {
                        _cachedAddress = baseAddress + (nuint)abs;
                        return snap;
                    }
                }
            }

            searchFrom = abs + V3Layout.MagicLen;
        }

        return null;
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
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;

        if (mbi.State != MEM_COMMIT) return false;
        if ((mbi.Protect & PAGE_NOACCESS) != 0) return false;
        if ((mbi.Protect & PAGE_GUARD) != 0) return false;
        return true;
    }
}

public sealed class ScannerStats
{
    public long StableHits;
    public long SmallWindowHits;
    public long FullScanHits;
    public long CrcFailures;
    public long TornReadRetries;
}
