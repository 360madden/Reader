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

    // The real marker is ~200 bytes. If end marker is further than this, it's a false match.
    private const int MaxMarkerLength = 1024;

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
                var snap = TryParseAndValidate(cached);
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

            if (queryResult == 0) break;

            nuint regionEnd = mbi.BaseAddress + mbi.RegionSize;

            if (IsReadable(mbi))
            {
                var result = ScanRegion(mbi.BaseAddress, (int)Math.Min(mbi.RegionSize, (nuint)MaxReadSize));
                if (result is not null) return result;
            }

            if (regionEnd <= address) break;
            address = regionEnd;
        }

        return null;
    }

    private ReaderSnapshot? ScanRegion(nuint baseAddress, int size)
    {
        byte[]? buf = ReadAt(baseAddress, size);
        if (buf is null) return null;

        ReadOnlySpan<byte> span = buf;
        int searchFrom = 0;

        // Iterate through ALL occurrences of the start marker in this region.
        // The first match is often the Lua source code literal — skip it.
        while (searchFrom < span.Length)
        {
            int idx = span[searchFrom..].IndexOf(StartMarker);
            if (idx < 0) break;

            int absoluteIdx = searchFrom + idx;

            // Check that the end marker is within a reasonable distance
            ReadOnlySpan<byte> candidate = span.Slice(absoluteIdx, Math.Min(MaxMarkerLength, span.Length - absoluteIdx));
            int endIdx = candidate.IndexOf(EndMarker);

            if (endIdx > 0)
            {
                var snap = TryParseAndValidate(candidate[..( endIdx + EndMarker.Length)]);
                if (snap is not null)
                {
                    _cachedAddress = baseAddress + (nuint)absoluteIdx;
                    return snap;
                }
            }

            // This match was a false positive (source literal, garbage, etc.) — skip past it
            searchFrom = absoluteIdx + StartMarker.Length;
        }

        return null;
    }

    /// <summary>
    /// Parses the buffer and validates the result is real player data, not source code garbage.
    /// </summary>
    private static ReaderSnapshot? TryParseAndValidate(ReadOnlySpan<byte> buffer)
    {
        var snap = MarkerParser.ParseFromBuffer(buffer);
        if (snap is null) return null;

        // Validate: player name must exist and contain only printable characters
        if (string.IsNullOrEmpty(snap.Player.Name)) return null;
        foreach (char c in snap.Player.Name)
        {
            if (char.IsControl(c)) return null;
        }

        // Validate: level must be a reasonable value (1-70 for RIFT)
        if (snap.Player.Level is null or < 1 or > 70) return null;

        // Validate: HP values should be non-negative if present
        if (snap.Stats.Hp < 0 || snap.Stats.HpMax < 0) return null;

        return snap;
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
        if (mbi.State != Kernel32.MemCommit) return false;

        uint protect = mbi.Protect;
        if ((protect & Kernel32.PageNoAccess) != 0) return false;
        if ((protect & Kernel32.PageGuard) != 0) return false;

        return true;
    }
}
