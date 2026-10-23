using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OneImage.Psd.Tests;

/// <summary>Builds small PSD and PSB files by hand, to test the reader against the format rather than against our own writer.</summary>
internal static class Psdb
{
    public static byte[] Flat(int width, int height, int depth, int mode, int channels, int compression, byte[] planesBe, bool psb = false, byte[]? icc = null, byte[]? colorModeData = null, bool alphaLayerCount = false)
    {
        var ms = new MemoryStream();
        void U16(int v) => ms.Write([(byte)(v >> 8), (byte)v]);
        void U32(uint v) => ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        ms.Write("8BPS"u8);
        U16(psb ? 2 : 1);
        ms.Write(new byte[6]);
        U16(channels);
        U32((uint)height);
        U32((uint)width);
        U16(depth);
        U16(mode);
        U32((uint)(colorModeData?.Length ?? 0));
        if (colorModeData != null)
        {
            ms.Write(colorModeData);
        }

        if (icc != null)
        {
            U32((uint)(12 + icc.Length + (icc.Length & 1)));
            ms.Write("8BIM"u8);
            U16(1039);
            U16(0);
            U32((uint)icc.Length);
            ms.Write(icc);
            if ((icc.Length & 1) != 0)
            {
                ms.WriteByte(0);
            }
        }
        else
        {
            U32(0);
        }

        void Len(uint v)
        {
            if (psb)
            {
                U32(0);
            }

            U32(v);
        }

        if (alphaLayerCount)
        {
            // A layer info with a negative count and no records: the merged image carries transparency.
            Len((uint)((psb ? 8 : 4) + 2 + 4));
            Len(2);
            U16(0xFFFF);
            U32(0);
        }
        else
        {
            Len(0);
        }

        U16(compression);
        int rowBytes = depth == 1 ? (width + 7) / 8 : width * depth / 8;
        switch (compression)
        {
            case 0:
                ms.Write(planesBe);
                break;
            case 1:
                {
                    int lines = channels * height;
                    var table = new MemoryStream();
                    var data = new MemoryStream();
                    byte[] packed = new byte[rowBytes * 2 + 4];
                    for (int l = 0; l < lines; l++)
                    {
                        int n = Rle.Pack(planesBe.AsSpan(l * rowBytes, rowBytes), packed);
                        if (psb)
                        {
                            table.Write([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
                        }
                        else
                        {
                            table.Write([(byte)(n >> 8), (byte)n]);
                        }

                        data.Write(packed, 0, n);
                    }

                    ms.Write(table.ToArray());
                    ms.Write(data.ToArray());
                    break;
                }

            default:
                {
                    byte[] input = compression == 3 ? Predict(planesBe, rowBytes, width, depth) : planesBe;
                    using var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true);
                    z.Write(input);
                    break;
                }
        }

        return ms.ToArray();
    }

    // The forward ZIP predictor: each line stores differences from the sample to its left (32-bit samples as byte planes first).
    public static byte[] Predict(byte[] planes, int rowBytes, int width, int depth)
    {
        byte[] output = (byte[])planes.Clone();
        for (int off = 0; off + rowBytes <= output.Length; off += rowBytes)
        {
            Span<byte> row = output.AsSpan(off, rowBytes);
            switch (depth)
            {
                case 8:
                    for (int i = row.Length - 1; i >= 1; i--)
                    {
                        row[i] = (byte)(row[i] - row[i - 1]);
                    }

                    break;
                case 16:
                    for (int i = row.Length - 2; i >= 2; i -= 2)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(row.Slice(i), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i)) - BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i - 2))));
                    }

                    break;
                default:
                    {
                        byte[] tmp = row.ToArray();
                        for (int x = 0; x < width; x++)
                        {
                            for (int b = 0; b < 4; b++)
                            {
                                row[b * width + x] = tmp[4 * x + b];
                            }
                        }

                        for (int i = row.Length - 1; i >= 1; i--)
                        {
                            row[i] = (byte)(row[i] - row[i - 1]);
                        }

                        break;
                    }
            }
        }

        return output;
    }

    public static byte[] Planes8(int width, int height, int channels, Func<int, int, int, byte> sample)
    {
        byte[] b = new byte[width * height * channels];
        for (int c = 0; c < channels; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    b[(c * height + y) * width + x] = sample(c, x, y);
                }
            }
        }

        return b;
    }

    public static byte[] Planes16(int width, int height, int channels, Func<int, int, int, ushort> sample)
    {
        byte[] b = new byte[width * height * channels * 2];
        for (int c = 0; c < channels; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(((c * height + y) * width + x) * 2), sample(c, x, y));
                }
            }
        }

        return b;
    }

    public static byte[] PlanesF(int width, int height, int channels, Func<int, int, int, float> sample)
    {
        byte[] b = new byte[width * height * channels * 4];
        for (int c = 0; c < channels; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(((c * height + y) * width + x) * 4), sample(c, x, y));
                }
            }
        }

        return b;
    }

    public static string Text(string s) => s;

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
