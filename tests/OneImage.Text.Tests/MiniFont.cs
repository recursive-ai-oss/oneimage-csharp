using System.Buffers.Binary;
using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

/// <summary>Builds tiny CFF and TrueType fonts around hand-written glyph programs, so limits can be tested with exact inputs.</summary>
internal static class MiniFont
{
    /// <summary>An OpenType font with name-keyed CFF outlines: glyph 0 is empty, glyph 1 is 'A' with the given charstring.</summary>
    public static byte[] Cff(byte[] glyph, byte[]? localSubr = null, byte[]? globalSubr = null)
    {
        byte[] charStrings = Index([[0x0E], glyph]);
        byte[] gsubrs = globalSubr is null ? Index([]) : Index([globalSubr]);
        byte[] header = [1, 0, 4, 1];
        byte[] names = Index(["T"u8.ToArray()]);
        byte[] strings = Index([]);

        // The private dictionary only holds the Subrs offset (relative to itself), and the subroutine index follows it directly.
        byte[] privDict = localSubr is null ? [] : [.. Int(6, 29), 19];
        byte[] subrIndex = localSubr is null ? [] : Index([localSubr]);

        // Top DICT operands are fixed width, so its size does not depend on the offsets it holds.
        int topIndexLen = Index([new byte[17]]).Length;
        int charStringsOff = header.Length + names.Length + topIndexLen + strings.Length + gsubrs.Length;
        int privOff = charStringsOff + charStrings.Length;
        byte[] top = [.. Int(charStringsOff, 29), 17, .. Int(privDict.Length, 29), .. Int(privOff, 29), 18];
        byte[] topIndex = Index([top]);

        byte[] cff = [.. header, .. names, .. topIndex, .. strings, .. gsubrs, .. charStrings, .. privDict, .. subrIndex];
        return Sfnt(0x4F54544F, ("CFF "u8.ToArray(), cff));
    }

    /// <summary>A TrueType font whose glyph 1 ('A') uses the given raw glyf data; glyph 0 is empty.</summary>
    public static byte[] TrueType(byte[] glyphData)
    {
        byte[] glyf = glyphData.Length % 2 == 0 ? glyphData : [.. glyphData, 0];
        byte[] loca = new byte[3 * 4];
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(8), (uint)glyf.Length);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4), 0);
        return Sfnt(0x00010000, ("glyf"u8.ToArray(), glyf), ("loca"u8.ToArray(), loca), localIndexToLoc: 1);
    }

    private static byte[] Sfnt(uint version, (byte[] Tag, byte[] Data) outline1, (byte[] Tag, byte[] Data)? outline2 = null, int localIndexToLoc = 0)
    {
        byte[] head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), (short)localIndexToLoc);
        byte[] hhea = new byte[36];
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), 2);
        byte[] maxp = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), 2);
        byte[] hmtx = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(0), 500);
        BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4), 600);
        byte[] cmap =
        [
            0, 0, 0, 1, 0, 3, 0, 1, 0, 0, 0, 12,
            0, 6, 0, 12, 0, 0, 0, 0x41, 0, 1, 0, 1,
        ];

        var tables = new List<(byte[] Tag, byte[] Data)>
        {
            ("head"u8.ToArray(), head),
            ("hhea"u8.ToArray(), hhea),
            ("maxp"u8.ToArray(), maxp),
            ("hmtx"u8.ToArray(), hmtx),
            ("cmap"u8.ToArray(), cmap),
            outline1,
        };
        if (outline2 is { } o2)
        {
            tables.Add(o2);
        }

        var ms = new MemoryStream();
        Span<byte> w = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(w, version);
        ms.Write(w);
        BinaryPrimitives.WriteUInt16BigEndian(w, (ushort)tables.Count);
        ms.Write(w[..2]);
        ms.Write(new byte[6]);
        int offset = 12 + (16 * tables.Count);
        foreach ((byte[] tag, byte[] data) in tables)
        {
            ms.Write(tag);
            ms.Write(new byte[4]);
            BinaryPrimitives.WriteUInt32BigEndian(w, (uint)offset);
            ms.Write(w);
            BinaryPrimitives.WriteUInt32BigEndian(w, (uint)data.Length);
            ms.Write(w);
            offset += (data.Length + 3) & ~3;
        }

        foreach ((_, byte[] data) in tables)
        {
            ms.Write(data);
            ms.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
        }

        return ms.ToArray();
    }

    private static byte[] Index(byte[][] items)
    {
        if (items.Length == 0)
        {
            return [0, 0];
        }

        var ms = new MemoryStream();
        ms.WriteByte((byte)(items.Length >> 8));
        ms.WriteByte((byte)items.Length);
        ms.WriteByte(4);
        int off = 1;
        Span<byte> w = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(w, (uint)off);
        ms.Write(w);
        foreach (byte[] item in items)
        {
            off += item.Length;
            BinaryPrimitives.WriteUInt32BigEndian(w, (uint)off);
            ms.Write(w);
        }

        foreach (byte[] item in items)
        {
            ms.Write(item);
        }

        return ms.ToArray();
    }

    private static byte[] Int(int value, byte op)
    {
        byte[] b = new byte[5];
        b[0] = op;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), value);
        return b;
    }

    /// <summary>A charstring number: 28 + int16.</summary>
    public static byte[] Num(int v) => [28, (byte)(v >> 8), (byte)v];
}
