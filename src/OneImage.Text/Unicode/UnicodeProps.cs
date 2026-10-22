using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OneImage.Text.Unicode;

/// <summary>Lookups into the generated property tables.</summary>
internal static class UnicodeProps
{
    // The tables cover U+0000..U+3FFFF and the U+E0000 block; every other code point has the default value.
    private const int LowBlocks = 0x40000 >> UnicodeData.BlockShift;
    private const int BlockMask = (1 << UnicodeData.BlockShift) - 1;

    /// <summary>Gets the packed line break value of a code point (see the generator for the bit layout).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LineBreakValue(int cp)
    {
        if ((uint)cp < 0x40000)
        {
            return U16(UnicodeData.LineBreakBlocks, (U16(UnicodeData.LineBreakIndex, cp >> UnicodeData.BlockShift) << UnicodeData.BlockShift) | (cp & BlockMask));
        }

        if ((uint)(cp - 0xE0000) < 0x1000)
        {
            int b = U16(UnicodeData.LineBreakIndex, LowBlocks + ((cp - 0xE0000) >> UnicodeData.BlockShift));
            return U16(UnicodeData.LineBreakBlocks, (b << UnicodeData.BlockShift) | (cp & BlockMask));
        }

        return UnicodeData.LineBreakDefault;
    }

    /// <summary>Gets the packed grapheme value of a code point (see the generator for the bit layout).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GraphemeValue(int cp)
    {
        if ((uint)cp < 0x40000)
        {
            return UnicodeData.GraphemeBlocks[(U16(UnicodeData.GraphemeIndex, cp >> UnicodeData.BlockShift) << UnicodeData.BlockShift) | (cp & BlockMask)];
        }

        if ((uint)(cp - 0xE0000) < 0x1000)
        {
            int b = U16(UnicodeData.GraphemeIndex, LowBlocks + ((cp - 0xE0000) >> UnicodeData.BlockShift));
            return UnicodeData.GraphemeBlocks[(b << UnicodeData.BlockShift) | (cp & BlockMask)];
        }

        return UnicodeData.GraphemeDefault;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int U16(ReadOnlySpan<byte> table, int index) => BinaryPrimitives.ReadUInt16LittleEndian(table.Slice(index * 2));
}
