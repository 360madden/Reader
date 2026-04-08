namespace Reader.Core;

/// <summary>
/// Byte-level layout constants for the v4 ReaderBridge wire format.
///
/// v4 doubles the BODY budget from 4096 to 8192 bytes to make room for
/// phase-2 sections (cooldowns, equipment, character stats, group, currency)
/// without compromising the existing buffs/combat headroom. Layout, offsets,
/// integrity guards, and double-buffering are otherwise unchanged from v3.
///
/// Top-level (16584 bytes total, fixed length):
///   [ MAGIC 8 ][ CONTROL 32 ][ SLOT_A 8272 ][ SLOT_B 8272 ]
///
/// MAGIC (8B):    "RBRG4\n=="
///
/// CONTROL (32B): byte offsets within the control block
///   0      : 'A' or 'B'   (active slot)
///   1      : '|'
///   2..17  : seq          (16 hex chars, u64)
///   18     : '|'
///   19..30 : t_ms         (12 hex chars, u48 monotonic ms)
///   31     : '\n'
///
/// SLOT (8272B):  [ SLOT_HDR 64 ][ BODY 8192 padded ][ CRC 8 ][ SLOT_END 8 ]
///
/// SLOT_HDR (64B): byte offsets within the slot header
///   0..1   : "SH"
///   2      : '|'
///   3..18  : seq          (16 hex)
///   19     : '|'
///   20..31 : t_ms         (12 hex)
///   32     : '|'
///   33..40 : flags        (8 hex)
///   41     : '|'
///   42..49 : len          (8 hex, payload byte length)
///   50     : '|'
///   51..58 : sec          (8 hex, section presence bitmask)
///   59     : '|'
///   60..61 : ver          ("04")
///   62     : '|'
///   63     : '\n'
///
/// BODY: variable-length, padded with '.' to <see cref="SlotBodyMax"/>.
/// Sections inside BODY have form:
///   [ TAG 1B ][ LEN 8 hex ][ payload ]
/// Section payload is `;`-separated key=value pairs; string values are
/// length-prefixed (`name=5:Aelyn`) so they cannot collide with delimiters.
///
/// CRC (8B):     CRC-32 of (SLOT_HDR + BODY[0..len]) as upper-case hex.
/// SLOT_END (8B): "==RBRG=="
/// </summary>
public static class V3Layout
{
    public static ReadOnlySpan<byte> Magic    => "RBRG4\n=="u8;
    public static ReadOnlySpan<byte> SlotEnd  => "==RBRG=="u8;

    public const int MagicLen      = 8;
    public const int ControlLen    = 32;
    public const int SlotHdrLen    = 64;
    public const int SlotBodyMax   = 8192;
    public const int CrcLen        = 8;
    public const int SlotEndLen    = 8;
    public const int SlotLen       = SlotHdrLen + SlotBodyMax + CrcLen + SlotEndLen; // 8272
    public const int TotalLen      = MagicLen + ControlLen + 2 * SlotLen;            // 16584

    // Top-level offsets
    public const int ControlOff    = MagicLen;                       // 8
    public const int SlotAOff      = MagicLen + ControlLen;          // 40
    public const int SlotBOff      = SlotAOff + SlotLen;             // 8312

    // Control field offsets (relative to start of control block)
    public const int CtrlActiveOff = 0;
    public const int CtrlSeqOff    = 2;
    public const int CtrlTOff      = 19;

    // Slot header field offsets (relative to start of slot)
    public const int HdrSeqOff     = 3;
    public const int HdrTOff       = 20;
    public const int HdrFlagsOff   = 33;
    public const int HdrLenOff     = 42;
    public const int HdrSecOff     = 51;
    public const int HdrVerOff     = 60;

    // Slot internal offsets
    public const int BodyOff       = SlotHdrLen;                     // 64
    public const int CrcOff        = SlotHdrLen + SlotBodyMax;       // 8256
    public const int SlotEndOff    = CrcOff + CrcLen;                // 8264

    // Section presence bits (matches Lua emitter)
    public const uint SecP = 1u << 0;  // Player
    public const uint SecT = 1u << 1;  // Target
    public const uint SecB = 1u << 2;  // Player buffs
    public const uint SecD = 1u << 3;  // Player debuffs
    public const uint Secb = 1u << 4;  // Target buffs
    public const uint Secd = 1u << 5;  // Target debuffs
    public const uint SecC = 1u << 6;  // Combat events
    public const uint SecS = 1u << 7;  // Stats
    public const uint SecZ = 1u << 8;  // Zone

    public const byte VerByte = (byte)'4';

    // ---------- Hex parse helpers (zero allocation) ----------

    public static bool TryParseHexU64(ReadOnlySpan<byte> hex, out ulong value)
    {
        ulong v = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            int d = HexDigit(hex[i]);
            if (d < 0) { value = 0; return false; }
            v = (v << 4) | (uint)d;
        }
        value = v;
        return true;
    }

    public static bool TryParseHexU32(ReadOnlySpan<byte> hex, out uint value)
    {
        if (TryParseHexU64(hex, out ulong v) && v <= uint.MaxValue)
        {
            value = (uint)v;
            return true;
        }
        value = 0;
        return false;
    }

    private static int HexDigit(byte b) =>
        b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => -1,
        };

    // ---------- Hex format helpers ----------

    private static readonly byte[] HexChars = "0123456789ABCDEF"u8.ToArray();

    public static void WriteHex(Span<byte> dest, ulong value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            dest[i] = HexChars[(int)(value & 0xF)];
            value >>= 4;
        }
    }
}
