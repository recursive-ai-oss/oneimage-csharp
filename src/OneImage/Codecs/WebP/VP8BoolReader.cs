using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OneImage.Codecs.WebP;

/// <summary>
/// The VP8 boolean entropy decoder (RFC 6386 section 7) with a 64-bit window. Reading past the end of the data yields
/// zero bits once and then sets <see cref="Eof"/>, which callers treat as truncation.
/// </summary>
internal struct VP8BoolReader
{
    private byte[] _buf;
    private int _pos;
    private int _end;
    private ulong _value;
    private uint _range;
    private int _bits;

    private bool _eof;

    public bool Eof => _eof;

    public void Init(byte[] buf, int start, int end)
    {
        _buf = buf;
        _pos = start;
        _end = end;
        _range = 255 - 1;
        _value = 0;
        _bits = -8;
        _eof = false;
        LoadNewBytes();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit(int prob)
    {
        uint range = _range;
        if (_bits < 0)
        {
            LoadNewBytes();
        }

        int pos = _bits;
        uint split = (range * (uint)prob) >> 8;
        uint value = (uint)(_value >> pos);
        int bit;
        if (value > split)
        {
            range -= split;
            _value -= (ulong)(split + 1) << pos;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        int shift = 7 ^ BitOperations.Log2(range);
        range <<= shift;
        _bits -= shift;
        _range = range - 1;
        return bit;
    }

    public int GetValue(int bits)
    {
        int v = 0;
        while (bits-- > 0)
        {
            v |= GetBit(0x80) << bits;
        }

        return v;
    }

    public int GetSignedValue(int bits)
    {
        int value = GetValue(bits);
        return GetBit(0x80) != 0 ? -value : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSigned(int v) => GetBit(0x80) != 0 ? -v : v;

    /// <summary>
    /// Hands the decoder state out so a hot loop can keep it in locals (the struct has more fields than the JIT keeps in
    /// registers). <see cref="Import"/> writes it back.
    /// </summary>
    public readonly void Export(out byte[] buf, out int pos, out int end, out ulong value, out uint range, out int bits, out bool eof)
    {
        buf = _buf;
        pos = _pos;
        end = _end;
        value = _value;
        range = _range;
        bits = _bits;
        eof = _eof;
    }

    /// <summary>Takes back the state changed by <see cref="BitLocal"/> calls.</summary>
    public void Import(int pos, ulong value, uint range, int bits, bool eof)
    {
        _pos = pos;
        _value = value;
        _range = range;
        _bits = bits;
        _eof = eof;
    }

    /// <summary>
    /// The same decoding step as <see cref="GetBit"/> over state held in the caller's locals. The refill is written out inline,
    /// with no call, so the JIT keeps the state in registers across the whole block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BitLocal(byte[] buf, ref int pos, int end, ref ulong value, ref uint range, ref int bits, ref bool eof, int prob)
    {
        uint r = range;
        if (bits < 0)
        {
            if (pos + 8 <= end)
            {
                // 56 bits at once, most significant byte first.
                value = (BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos)) >> 8) | (value << 56);
                pos += 7;
                bits += 56;
            }
            else if (pos < end)
            {
                bits += 8;
                value = buf[pos++] | (value << 8);
            }
            else if (!eof)
            {
                value <<= 8;
                bits += 8;
                eof = true;
            }
            else
            {
                bits = 0;
            }
        }

        int sh = bits;
        uint split = (r * (uint)prob) >> 8;
        uint v = (uint)(value >> sh);
        int bit;
        if (v > split)
        {
            r -= split;
            value -= (ulong)(split + 1) << sh;
            bit = 1;
        }
        else
        {
            r = split + 1;
            bit = 0;
        }

        int shift = 7 ^ BitOperations.Log2(r);
        r <<= shift;
        bits -= shift;
        range = r - 1;
        return bit;
    }

    private void LoadNewBytes()
    {
        if (_pos + 8 <= _end)
        {
            // 56 bits at once, most significant byte first.
            ulong bits = BinaryPrimitives.ReadUInt64BigEndian(_buf.AsSpan(_pos)) >> 8;
            _pos += 7;
            _value = bits | (_value << 56);
            _bits += 56;
        }
        else if (_pos < _end)
        {
            _bits += 8;
            _value = _buf[_pos++] | (_value << 8);
        }
        else if (!_eof)
        {
            _value <<= 8;
            _bits += 8;
            _eof = true;
        }
        else
        {
            _bits = 0;
        }
    }
}
