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

    public bool Eof { get; private set; }

    public void Init(byte[] buf, int start, int end)
    {
        _buf = buf;
        _pos = start;
        _end = end;
        _range = 255 - 1;
        _value = 0;
        _bits = -8;
        Eof = false;
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
        else if (!Eof)
        {
            _value <<= 8;
            _bits += 8;
            Eof = true;
        }
        else
        {
            _bits = 0;
        }
    }
}
