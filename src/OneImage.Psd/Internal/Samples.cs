using System.Buffers.Binary;

namespace OneImage.Psd;

/// <summary>The sample type a band is converted to: the sample type of the document or frame being filled.</summary>
internal enum SampleKind
{
    U8,
    U16,
    F32,
}

/// <summary>Conversions of one scan line of big-endian PSD samples (1, 8, 16 or 32 bits) to the sample type a document stores.</summary>
internal static class Samples
{
    public static void ToU8(ReadOnlySpan<byte> row, int depth, int count, Span<byte> dst)
    {
        switch (depth)
        {
            case 1:
                for (int x = 0; x < count; x++)
                {
                    // In a bitmap, 1 is black.
                    dst[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
                }

                break;
            case 8:
                row.Slice(0, count).CopyTo(dst);
                break;
            case 16:
                for (int x = 0; x < count; x++)
                {
                    uint v = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(x * 2));
                    dst[x] = (byte)((v * 255 + 32767) / 65535);
                }

                break;
            default:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = F32To8(BinaryPrimitives.ReadSingleBigEndian(row.Slice(x * 4)));
                }

                break;
        }
    }

    public static void ToU16(ReadOnlySpan<byte> row, int depth, int count, Span<ushort> dst)
    {
        switch (depth)
        {
            case 1:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (ushort)0 : (ushort)65535;
                }

                break;
            case 8:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = (ushort)(row[x] * 257);
                }

                break;
            case 16:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(x * 2));
                }

                break;
            default:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = F32To16(BinaryPrimitives.ReadSingleBigEndian(row.Slice(x * 4)));
                }

                break;
        }
    }

    public static void ToF32(ReadOnlySpan<byte> row, int depth, int count, Span<float> dst)
    {
        switch (depth)
        {
            case 1:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? 0f : 1f;
                }

                break;
            case 8:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = row[x] / 255f;
                }

                break;
            case 16:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(x * 2)) / 65535f;
                }

                break;
            default:
                for (int x = 0; x < count; x++)
                {
                    dst[x] = BinaryPrimitives.ReadSingleBigEndian(row.Slice(x * 4));
                }

                break;
        }
    }

    public static byte F32To8(float f) => float.IsNaN(f) ? (byte)0 : (byte)(Math.Clamp(f, 0f, 1f) * 255f + 0.5f);

    public static ushort F32To16(float f) => float.IsNaN(f) ? (ushort)0 : (ushort)(Math.Clamp(f, 0f, 1f) * 65535f + 0.5f);
}

/// <summary>Color planes of a band of rows in one sample type: one plane for gray, three for RGB, plus an optional alpha plane.</summary>
internal sealed class Planes
{
    public byte[][]? U8;
    public ushort[][]? U16;
    public float[][]? F32;
    public int Count;
    public bool HasAlpha;
}
