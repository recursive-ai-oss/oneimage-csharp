using System.Buffers.Binary;

namespace OneImage.Kernels;

/// <summary>How a raw PNG scanline (after unfiltering) becomes library pixels.</summary>
internal enum PngUnpackMode
{
    /// <summary>8-bit samples whose layout already matches the output (gray, gray+alpha, RGB, RGBA).</summary>
    Copy,

    /// <summary>16-bit samples, byte-swapped to little endian (gray, RGB, RGBA).</summary>
    Swap16,

    /// <summary>1/2/4/8-bit gray scaled to 8 bits; with a transparent key the output is gray+alpha.</summary>
    LowGray,

    /// <summary>1/2/4/8-bit palette indices expanded through the palette to RGB or RGBA.</summary>
    Palette,

    /// <summary>8-bit RGB with a transparent color key, output RGBA.</summary>
    Rgb8Key,

    /// <summary>16-bit gray, output RGBA 16 (gray replicated), alpha from the key or the alpha sample.</summary>
    Gray16ToRgba,

    /// <summary>16-bit RGB with a transparent color key, output RGBA 16.</summary>
    Rgb16Key,
}

/// <summary>Per-image constants for <see cref="PngPixels.Unpack"/>.</summary>
internal sealed class PngUnpackContext
{
    public PngUnpackMode Mode;
    public int BitDepth;
    public bool HasAlphaSample;

    /// <summary>Transparent color key (gray, or red/green/blue), or -1 when there is none.</summary>
    public int KeyR = -1;
    public int KeyG = -1;
    public int KeyB = -1;

    /// <summary>256 RGBA entries (4 bytes each), unused entries opaque black.</summary>
    public byte[] Palette = new byte[1024];

    public bool PaletteHasAlpha;
}

/// <summary>Pixel-level row kernels for PNG: unpacking, byte order, and APNG blending.</summary>
internal static class PngPixels
{
    public static void Unpack(PngUnpackContext c, ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        switch (c.Mode)
        {
            case PngUnpackMode.Copy:
                src[..dst.Length].CopyTo(dst);
                break;
            case PngUnpackMode.Swap16:
                {
                    int n = dst.Length;
                    BinaryPrimitives.ReverseEndianness(SpanCast.As<ushort>(src[..n]), SpanCast.As<ushort>(dst[..n]));
                    break;
                }

            case PngUnpackMode.LowGray:
                UnpackLowGray(c, src, dst, width);
                break;
            case PngUnpackMode.Palette:
                UnpackPalette(c, src, dst, width);
                break;
            case PngUnpackMode.Rgb8Key:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src.Slice(x * 3, 3);
                    Span<byte> d = dst.Slice(x * 4, 4);
                    s.CopyTo(d);
                    d[3] = s[0] == c.KeyR && s[1] == c.KeyG && s[2] == c.KeyB ? (byte)0 : (byte)255;
                }

                break;
            case PngUnpackMode.Gray16ToRgba:
                {
                    Span<ushort> d = SpanCast.As<ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        int o = c.HasAlphaSample ? x * 4 : x * 2;
                        ushort g = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(o, 2));
                        ushort a = c.HasAlphaSample ? BinaryPrimitives.ReadUInt16BigEndian(src.Slice(o + 2, 2)) : (g == c.KeyR ? (ushort)0 : (ushort)65535);
                        Span<ushort> px = d.Slice(x * 4, 4);
                        px[0] = g;
                        px[1] = g;
                        px[2] = g;
                        px[3] = a;
                    }

                    break;
                }

            case PngUnpackMode.Rgb16Key:
                {
                    Span<ushort> d = SpanCast.As<ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        ReadOnlySpan<byte> s = src.Slice(x * 6, 6);
                        ushort r = BinaryPrimitives.ReadUInt16BigEndian(s);
                        ushort g = BinaryPrimitives.ReadUInt16BigEndian(s[2..]);
                        ushort b = BinaryPrimitives.ReadUInt16BigEndian(s[4..]);
                        Span<ushort> px = d.Slice(x * 4, 4);
                        px[0] = r;
                        px[1] = g;
                        px[2] = b;
                        px[3] = r == c.KeyR && g == c.KeyG && b == c.KeyB ? (ushort)0 : (ushort)65535;
                    }

                    break;
                }
        }
    }

    private static void UnpackLowGray(PngUnpackContext c, ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int bits = c.BitDepth;
        int mask = (1 << bits) - 1;
        int scale = 255 / mask;
        bool key = c.KeyR >= 0;
        for (int x = 0; x < width; x++)
        {
            int bitPos = x * bits;
            int v = (src[bitPos >> 3] >> (8 - bits - (bitPos & 7))) & mask;
            if (key)
            {
                dst[x * 2] = (byte)(v * scale);
                dst[(x * 2) + 1] = v == c.KeyR ? (byte)0 : (byte)255;
            }
            else
            {
                dst[x] = (byte)(v * scale);
            }
        }
    }

    private static void UnpackPalette(PngUnpackContext c, ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int bits = c.BitDepth;
        int mask = (1 << bits) - 1;
        byte[] pal = c.Palette;
        if (c.PaletteHasAlpha)
        {
            for (int x = 0; x < width; x++)
            {
                int bitPos = x * bits;
                int idx = (src[bitPos >> 3] >> (8 - bits - (bitPos & 7))) & mask;
                pal.AsSpan(idx * 4, 4).CopyTo(dst.Slice(x * 4, 4));
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                int bitPos = x * bits;
                int idx = (src[bitPos >> 3] >> (8 - bits - (bitPos & 7))) & mask;
                pal.AsSpan(idx * 4, 3).CopyTo(dst.Slice(x * 3, 3));
            }
        }
    }

    /// <summary>Byte-swaps 16-bit samples between little endian (library) and big endian (PNG). Works in both directions.</summary>
    public static void Swap16(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = src.Length & ~1;
        BinaryPrimitives.ReverseEndianness(SpanCast.As<ushort>(src[..n]), SpanCast.As<ushort>(dst[..n]));
    }

    /// <summary>
    /// APNG blend op "over" for straight-alpha RGBA 8: composites <paramref name="src"/> onto <paramref name="dst"/>.
    /// Result is round-to-nearest of the exact straight-alpha Porter-Duff source-over.
    /// </summary>
    public static void Over8(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<byte> s = src.Slice(x * 4, 4);
            int sa = s[3];
            if (sa == 0)
            {
                continue;
            }

            Span<byte> d = dst.Slice(x * 4, 4);
            int da = d[3];
            if (sa == 255 || da == 0)
            {
                s.CopyTo(d);
                continue;
            }

            uint den = (uint)((sa * 255) + (da * (255 - sa)));
            uint half = den >> 1;
            uint sw = (uint)(sa * 255);
            uint dw = (uint)(da * (255 - sa));
            d[0] = (byte)(((s[0] * sw) + (d[0] * dw) + half) / den);
            d[1] = (byte)(((s[1] * sw) + (d[1] * dw) + half) / den);
            d[2] = (byte)(((s[2] * sw) + (d[2] * dw) + half) / den);
            d[3] = (byte)((den + 127) / 255);
        }
    }

    /// <summary>Like <see cref="Over8"/> for straight-alpha RGBA 16 (little-endian samples).</summary>
    public static void Over16(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        ReadOnlySpan<ushort> s16 = SpanCast.As<ushort>(src);
        Span<ushort> d16 = SpanCast.As<ushort>(dst);
        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<ushort> s = s16.Slice(x * 4, 4);
            ulong sa = s[3];
            if (sa == 0)
            {
                continue;
            }

            Span<ushort> d = d16.Slice(x * 4, 4);
            ulong da = d[3];
            if (sa == 65535 || da == 0)
            {
                s.CopyTo(d);
                continue;
            }

            ulong den = (sa * 65535) + (da * (65535 - sa));
            ulong half = den >> 1;
            ulong sw = sa * 65535;
            ulong dw = da * (65535 - sa);
            d[0] = (ushort)(((s[0] * sw) + (d[0] * dw) + half) / den);
            d[1] = (ushort)(((s[1] * sw) + (d[1] * dw) + half) / den);
            d[2] = (ushort)(((s[2] * sw) + (d[2] * dw) + half) / den);
            d[3] = (ushort)((den + 32767) / 65535);
        }
    }
}
