namespace OneImage.Codecs.Jpeg;

/// <summary>Marker codes and constant tables shared by the JPEG decoder and encoder (ITU-T T.81).</summary>
internal static class JpegConstants
{
    public const int Soi = 0xD8;
    public const int Eoi = 0xD9;
    public const int Sos = 0xDA;
    public const int Dqt = 0xDB;
    public const int Dnl = 0xDC;
    public const int Dri = 0xDD;
    public const int Dht = 0xC4;
    public const int Dac = 0xCC;
    public const int Sof0 = 0xC0;
    public const int Sof1 = 0xC1;
    public const int Sof2 = 0xC2;
    public const int Sof9 = 0xC9;
    public const int Sof10 = 0xCA;
    public const int App0 = 0xE0;
    public const int App1 = 0xE1;
    public const int App2 = 0xE2;
    public const int App13 = 0xED;
    public const int App14 = 0xEE;
    public const int Com = 0xFE;

    /// <summary>Gets, for each zigzag position, the index in natural (row-major) order (T.81 Figure A.6).</summary>
    public static ReadOnlySpan<byte> Zigzag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Luminance quantization table at quality 50, natural order (T.81 Table K.1).</summary>
    public static ReadOnlySpan<byte> StdLuminanceQuant =>
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    /// <summary>Chrominance quantization table at quality 50, natural order (T.81 Table K.2).</summary>
    public static ReadOnlySpan<byte> StdChrominanceQuant =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>Code length counts (lengths 1..16) of the standard DC luminance table (T.81 Table K.3).</summary>
    public static ReadOnlySpan<byte> DcLumaBits => [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Symbols of the standard DC luminance table.</summary>
    public static ReadOnlySpan<byte> DcLumaVals => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    /// <summary>Code length counts of the standard DC chrominance table (T.81 Table K.4).</summary>
    public static ReadOnlySpan<byte> DcChromaBits => [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];

    /// <summary>Symbols of the standard DC chrominance table.</summary>
    public static ReadOnlySpan<byte> DcChromaVals => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    /// <summary>Code length counts of the standard AC luminance table (T.81 Table K.5).</summary>
    public static ReadOnlySpan<byte> AcLumaBits => [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    /// <summary>Symbols of the standard AC luminance table.</summary>
    public static ReadOnlySpan<byte> AcLumaVals =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    /// <summary>Code length counts of the standard AC chrominance table (T.81 Table K.6).</summary>
    public static ReadOnlySpan<byte> AcChromaBits => [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    /// <summary>Symbols of the standard AC chrominance table.</summary>
    public static ReadOnlySpan<byte> AcChromaVals =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];
}

/// <summary>A Huffman table prepared for decoding: a 9-bit lookup for short codes and canonical limits for long ones (T.81 F.2.2.3).</summary>
internal sealed class JpegHuffmanTable
{
    public const int FastBits = 9;

    /// <summary>Entries are <c>(length &lt;&lt; 8) | symbol</c>, or 0 when the code is longer than <see cref="FastBits"/>.</summary>
    public readonly ushort[] Fast = new ushort[1 << FastBits];

    /// <summary>Largest code of each length, or -1.</summary>
    public readonly int[] MaxCode = new int[18];

    public readonly int[] MinCode = new int[17];

    public readonly int[] ValPtr = new int[17];

    public readonly byte[] Values = new byte[256];

    public int Count;

    /// <summary>The raw length counts and symbols, kept for the encoder to write the table back.</summary>
    public readonly byte[] Bits = new byte[17];

    /// <summary>Builds the table from DHT contents.</summary>
    /// <param name="counts">Sixteen code-length counts (index 0 = length 1).</param>
    /// <param name="values">The symbols in code order.</param>
    /// <exception cref="ImageFormatException">The lengths do not describe a valid prefix code.</exception>
    public JpegHuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        int total = 0;
        for (int l = 1; l <= 16; l++)
        {
            Bits[l] = counts[l - 1];
            total += counts[l - 1];
        }

        if (total > 256 || total > values.Length)
        {
            throw new ImageFormatException("Invalid JPEG Huffman table: too many symbols.");
        }

        Count = total;
        values[..total].CopyTo(Values);
        int code = 0;
        int k = 0;
        for (int l = 1; l <= 16; l++)
        {
            int n = Bits[l];
            ValPtr[l] = k;
            MinCode[l] = code;
            if (code + n > (1 << l))
            {
                throw new ImageFormatException("Invalid JPEG Huffman table: oversubscribed code lengths.");
            }

            for (int i = 0; i < n; i++)
            {
                if (l <= FastBits)
                {
                    int first = (code + i) << (FastBits - l);
                    int span = 1 << (FastBits - l);
                    ushort entry = (ushort)((l << 8) | Values[k + i]);
                    for (int j = 0; j < span; j++)
                    {
                        Fast[first + j] = entry;
                    }
                }
            }

            code += n;
            k += n;
            MaxCode[l] = n > 0 ? code - 1 : -1;
            code <<= 1;
        }

        MaxCode[17] = int.MaxValue;
    }
}
