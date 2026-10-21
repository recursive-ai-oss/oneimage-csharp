using System.Buffers.Binary;
using ImageMagick;
using OneImage.Tests.Png;

namespace OneImage.Tests.Bmp;

/// <summary>Helpers shared by the BMP and ICO tests.</summary>
internal static class BmpTestUtil
{
    public static Decoded Decode(IImageFormat format, byte[] file, int frame = 0, ImageLimits? limits = null) => Png.PngTestUtil.DecodeWith(format, file, frame, limits);

    public static Decoded DecodeBmp(byte[] file, ImageLimits? limits = null) => Decode(BmpFormat.Instance, file, 0, limits);

    /// <summary>Straight RGBA8 of any decoded frame.</summary>
    public static byte[] ToRgba8(Decoded d) => d.Format == PixelFormat.Rgba8 ? d.Data : Expand(d);

    private static byte[] Expand(Decoded d)
    {
        Assert.Equal(PixelFormat.Rgb8, d.Format);
        byte[] o = new byte[d.Width * d.Height * 4];
        for (int i = 0; i < d.Width * d.Height; i++)
        {
            d.Data.AsSpan(i * 3, 3).CopyTo(o.AsSpan(i * 4, 3));
            o[(i * 4) + 3] = 255;
        }

        return o;
    }

    /// <summary>Decodes with ImageMagick to straight RGBA8.</summary>
    public static byte[] MagickRgba(byte[] file, out int w, out int h)
    {
        using var img = new MagickImage(file);
        w = (int)img.Width;
        h = (int)img.Height;
        using var px = img.GetPixels();
        byte[] data = px.ToByteArray(PixelMapping.RGBA)!;
        return data;
    }

    public static byte[] MagickWrite(byte[] rgba, int w, int h, MagickFormat format, Action<MagickImage>? tweak = null)
    {
        using var img = new MagickImage(rgba, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));
        tweak?.Invoke(img);
        img.Format = format;
        return img.ToByteArray();
    }

    /// <summary>Few-color image: a gradient quantized to <paramref name="colors"/> colors by the caller's choice of palette.</summary>
    public static byte[] Colorful(int w, int h, int colors, bool alpha, int seed)
    {
        var rnd = new Random(seed);
        var pal = Enumerable.Range(0, colors).Select(_ => new byte[] { (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), alpha ? (byte)rnd.Next(256) : (byte)255 }).ToArray();
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pal[((i / 3) + (i / w)) % colors].CopyTo(px, i * 4);
        }

        return px;
    }

    // ---- Hand-built BMP files ----

    /// <summary>Assembles a BMP file from a DIB header (already serialized), extra tables and the pixel bytes.</summary>
    public static byte[] File(byte[] dibHeader, byte[] tables, byte[] pixels, byte[]? trailing = null)
    {
        int off = 14 + dibHeader.Length + tables.Length;
        byte[] f = new byte[off + pixels.Length + (trailing?.Length ?? 0)];
        f[0] = (byte)'B';
        f[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(2), (uint)f.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(10), (uint)off);
        dibHeader.CopyTo(f, 14);
        tables.CopyTo(f, 14 + dibHeader.Length);
        pixels.CopyTo(f, off);
        trailing?.CopyTo(f, off + pixels.Length);
        return f;
    }

    public static byte[] InfoHeader(int size, int w, int h, int bpp, int compression, int colorsUsed = 0)
    {
        byte[] d = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)size);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), h);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), (ushort)bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)compression);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), (uint)colorsUsed);
        return d;
    }

    public static void SetMasks(byte[] header, uint r, uint g, uint b, uint a)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), r);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), g);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), b);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), a);
    }

    public static byte[] PaletteBgrx(params (byte R, byte G, byte B)[] colors) => colors.SelectMany(c => new byte[] { c.B, c.G, c.R, 0 }).ToArray();

    public static int Scale(uint v, int bits) => bits == 0 ? 0 : (int)(((v * 255L) + (((1L << bits) - 1) / 2)) / ((1L << bits) - 1));
}
