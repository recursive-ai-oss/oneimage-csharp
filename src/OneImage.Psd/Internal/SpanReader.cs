using System.Buffers.Binary;
using System.Text;

namespace OneImage.Psd;

/// <summary>A bounds-checked big-endian cursor over bytes. Every read that would pass the end throws <see cref="ImageFormatException"/>.</summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly int Position => _pos;

    public readonly int Remaining => _data.Length - _pos;

    public readonly bool AtEnd => _pos >= _data.Length;

    private void Need(long n)
    {
        if (n < 0 || n > _data.Length - _pos)
        {
            throw new ImageFormatException("The PSD data ends before a field is complete.");
        }
    }

    public byte U8()
    {
        Need(1);
        return _data[_pos++];
    }

    public ushort U16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_pos));
        _pos += 2;
        return v;
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_pos));
        _pos += 4;
        return v;
    }

    public int I32() => (int)U32();

    public ulong U64()
    {
        Need(8);
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_pos));
        _pos += 8;
        return v;
    }

    public double F64() => BitConverter.Int64BitsToDouble((long)U64());

    /// <summary>Reads a length that is 4 bytes in PSD and 8 bytes in PSB; it must fit an int range of the remaining data.</summary>
    public long Length(bool wide)
    {
        ulong v = wide ? U64() : U32();
        if (v > long.MaxValue)
        {
            throw new ImageFormatException("A length field is too large.");
        }

        return (long)v;
    }

    public ReadOnlySpan<byte> Bytes(long n)
    {
        Need(n);
        ReadOnlySpan<byte> s = _data.Slice(_pos, (int)n);
        _pos += (int)n;
        return s;
    }

    public void Skip(long n)
    {
        Need(n);
        _pos += (int)n;
    }

    /// <summary>Moves to an absolute position inside the data.</summary>
    public void Seek(int position)
    {
        if ((uint)position > (uint)_data.Length)
        {
            throw new ImageFormatException("The PSD data ends before a field is complete.");
        }

        _pos = position;
    }

    public uint Tag() => U32();

    /// <summary>Reads a Pascal string (1-byte length) padded so the whole field is a multiple of <paramref name="multiple"/> bytes, as Latin-1.</summary>
    public string PascalString(int multiple)
    {
        int len = U8();
        ReadOnlySpan<byte> s = Bytes(len);
        int total = len + 1;
        int pad = (multiple - (total % multiple)) % multiple;
        Skip(Math.Min(pad, Remaining));
        return Encoding.Latin1.GetString(s);
    }

    /// <summary>Reads a Unicode string: a 4-byte count of UTF-16 code units, then the units. A trailing NUL is dropped.</summary>
    public string UnicodeString()
    {
        uint count = U32();
        if (count > (uint)(Remaining / 2))
        {
            throw new ImageFormatException("A Unicode string extends past the data.");
        }

        ReadOnlySpan<byte> s = Bytes(count * 2L);
        string text = Encoding.BigEndianUnicode.GetString(s);
        return text.TrimEnd('\0');
    }
}

internal static class Fourcc
{
    public static uint Of(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    public static string Name(uint v) => new([(char)(v >> 24 & 0xFF), (char)(v >> 16 & 0xFF), (char)(v >> 8 & 0xFF), (char)(v & 0xFF)]);
}
