using System.Buffers.Binary;

namespace OneImage.Text.Fonts;

/// <summary>Bounds-checked big-endian reads over a table. Every offset that comes from the font goes through here.</summary>
internal static class Be
{
    public static byte U8(ReadOnlySpan<byte> d, int o)
    {
        if ((uint)o >= (uint)d.Length)
        {
            throw Truncated();
        }

        return d[o];
    }

    public static sbyte I8(ReadOnlySpan<byte> d, int o) => (sbyte)U8(d, o);

    public static ushort U16(ReadOnlySpan<byte> d, int o)
    {
        if (o < 0 || o > d.Length - 2)
        {
            throw Truncated();
        }

        return BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o, 2));
    }

    public static short I16(ReadOnlySpan<byte> d, int o) => (short)U16(d, o);

    public static uint U24(ReadOnlySpan<byte> d, int o)
    {
        if (o < 0 || o > d.Length - 3)
        {
            throw Truncated();
        }

        return (uint)((d[o] << 16) | (d[o + 1] << 8) | d[o + 2]);
    }

    public static uint U32(ReadOnlySpan<byte> d, int o)
    {
        if (o < 0 || o > d.Length - 4)
        {
            throw Truncated();
        }

        return BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4));
    }

    public static int I32(ReadOnlySpan<byte> d, int o) => (int)U32(d, o);

    /// <summary>Returns <c>d[offset..offset+length]</c> or throws; both numbers may be hostile.</summary>
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> d, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > d.Length || length > d.Length - offset)
        {
            throw Truncated();
        }

        return d.Slice((int)offset, (int)length);
    }

    /// <summary>Reads an unsigned big-endian integer of 1 to 4 bytes.</summary>
    public static uint UN(ReadOnlySpan<byte> d, int o, int size) => size switch
    {
        1 => U8(d, o),
        2 => U16(d, o),
        3 => U24(d, o),
        4 => U32(d, o),
        _ => throw new FontFormatException("Invalid integer size."),
    };

    public static FontFormatException Truncated() => new("The font data is truncated or an offset points outside its table.");

    public static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
}
