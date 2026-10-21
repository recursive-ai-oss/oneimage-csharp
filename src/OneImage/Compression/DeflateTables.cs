using System;

namespace OneImage.Compression;

/// <summary>Constant tables from RFC 1951 shared by the inflater and the deflater.</summary>
internal static class DeflateTables
{
    /// <summary>Base match length for length symbols 257..285 (RFC 1951 3.2.5).</summary>
    public static readonly ushort[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
    };

    /// <summary>Extra bits for length symbols 257..285.</summary>
    public static readonly byte[] LengthExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    };

    /// <summary>Base distance for distance symbols 0..29.</summary>
    public static readonly ushort[] DistBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    };

    /// <summary>Extra bits for distance symbols 0..29.</summary>
    public static readonly byte[] DistExtra =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    };

    /// <summary>Order in which code-length code lengths are transmitted (RFC 1951 3.2.7).</summary>
    public static readonly byte[] ClenOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    /// <summary>Maps match length minus 3 (0..255) to the index of its length symbol (0..28).</summary>
    public static readonly byte[] LengthSymbol = BuildLengthSymbol();

    /// <summary>
    /// Maps (distance - 1) to a distance symbol: direct for values below 256, otherwise indexed by
    /// 256 + ((distance - 1) &gt;&gt; 7).
    /// </summary>
    public static readonly byte[] DistSymbol = BuildDistSymbol();

    /// <summary>Returns the distance symbol for a distance of 1..32768.</summary>
    public static int DistanceSymbol(int distance)
    {
        int d = distance - 1;
        return d < 256 ? DistSymbol[d] : DistSymbol[256 + (d >> 7)];
    }

    private static byte[] BuildLengthSymbol()
    {
        var t = new byte[256];
        int code = 0;
        for (int len = 3; len <= 258; len++)
        {
            if (code < 28 && len >= LengthBase[code + 1])
            {
                code++;
            }

            t[len - 3] = (byte)code;
        }

        // Length 258 has its own symbol (285) rather than being the top of symbol 284's range.
        t[255] = 28;
        return t;
    }

    private static byte[] BuildDistSymbol()
    {
        var t = new byte[256 + 256];
        int code = 0;
        for (int d = 0; d < 32768; d++)
        {
            while (code < 29 && d + 1 >= DistBase[code + 1])
            {
                code++;
            }

            if (d < 256)
            {
                t[d] = (byte)code;
            }
            else
            {
                t[256 + (d >> 7)] = (byte)code;
            }
        }

        return t;
    }
}
