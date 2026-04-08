using System.Text;

namespace Reader.Core;

/// <summary>
/// Walks the inside of a v3 section payload.
///
/// Section payload format:
///   key1=value1;key2=value2;...
///
/// String values are length-prefixed in **bytes**:
///   name=5:Aelyn
///                ^ 5 bytes follow
///
/// String values may therefore contain '=' or ';' without escaping. The walker
/// always advances exactly N bytes after the ':' for length-prefixed values,
/// and to the next ';' for unprefixed (numeric) values.
/// </summary>
public ref struct V3SectionWalker
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public V3SectionWalker(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    /// <summary>
    /// Reads the next key=value pair. Returns false at end-of-section.
    /// </summary>
    public bool TryNext(out ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value, out bool isLengthPrefixed)
    {
        key = default;
        value = default;
        isLengthPrefixed = false;

        if (_pos >= _data.Length) return false;

        // skip any leading ';'
        while (_pos < _data.Length && _data[_pos] == (byte)';') _pos++;
        if (_pos >= _data.Length) return false;

        int keyStart = _pos;
        while (_pos < _data.Length && _data[_pos] != (byte)'=') _pos++;
        if (_pos >= _data.Length) return false;

        key = _data.Slice(keyStart, _pos - keyStart);
        _pos++; // skip '='

        // Detect length-prefix: digits followed by ':'.
        int look = _pos;
        int len = 0;
        bool sawDigit = false;
        while (look < _data.Length)
        {
            byte b = _data[look];
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                len = len * 10 + (b - '0');
                sawDigit = true;
                look++;
                continue;
            }
            break;
        }

        if (sawDigit && look < _data.Length && _data[look] == (byte)':')
        {
            // length-prefixed value: exactly `len` bytes after the ':'
            int valStart = look + 1;
            int valEnd = valStart + len;
            if (valEnd > _data.Length) return false;
            value = _data.Slice(valStart, len);
            isLengthPrefixed = true;
            _pos = valEnd;
            // consume optional trailing ';'
            if (_pos < _data.Length && _data[_pos] == (byte)';') _pos++;
            return true;
        }

        // unprefixed (numeric) value: read until ';' or end
        int numStart = _pos;
        while (_pos < _data.Length && _data[_pos] != (byte)';') _pos++;
        value = _data.Slice(numStart, _pos - numStart);
        if (_pos < _data.Length) _pos++; // skip ';'
        return true;
    }

    public static string DecodeString(ReadOnlySpan<byte> bytes)
        => Encoding.UTF8.GetString(bytes);

    public static int? DecodeInt(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        int sign = 1;
        int i = 0;
        if (bytes[0] == (byte)'-') { sign = -1; i = 1; }
        long v = 0;
        for (; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b < (byte)'0' || b > (byte)'9') return null;
            v = v * 10 + (b - '0');
            if (v > int.MaxValue) return null;
        }
        return (int)(v * sign);
    }

    public static long? DecodeLong(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        int sign = 1;
        int i = 0;
        if (bytes[0] == (byte)'-') { sign = -1; i = 1; }
        long v = 0;
        for (; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b < (byte)'0' || b > (byte)'9') return null;
            v = v * 10 + (b - '0');
        }
        return v * sign;
    }

    public static float? DecodeFloat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        string s = Encoding.ASCII.GetString(bytes);
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;
    }

    public static double? DecodeDouble(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        string s = Encoding.ASCII.GetString(bytes);
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    public static bool DecodeBool(ReadOnlySpan<byte> bytes)
        => bytes.Length == 1 && bytes[0] == (byte)'1';
}
