using System.Runtime.InteropServices;
using SkiaSharp;

namespace OneImage.Tests.WebP;

/// <summary>Helpers for the WebP tests: deterministic test images and libwebp (through SkiaSharp) as the reference codec.</summary>
internal static class WebPOracle
{
    /// <summary>A photo-like RGBA test image: smooth gradients, hard edges, texture and noise. Alpha is a ramp when requested.</summary>
    public static byte[] MakeImage(int w, int h, int seed, bool alpha = false, bool noisy = true)
    {
        var rnd = new Random(seed);
        byte[] px = new byte[w * h * 4];
        double fx = 0.5 + (rnd.NextDouble() * 3);
        double fy = 0.5 + (rnd.NextDouble() * 3);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double u = (double)x / w;
                double v = (double)y / h;
                double r = 128 + (100 * Math.Sin((u * fx * 6.28) + (v * 2)));
                double g = 128 + (100 * Math.Cos((v * fy * 6.28) - (u * 3)));
                double b = 255 * (u * v);
                if (((x / 13) + (y / 17)) % 5 == 0)
                {
                    r = 255 - r;
                    b = 255 - b;
                }

                if (Math.Abs(u - 0.5) + Math.Abs(v - 0.5) < 0.2)
                {
                    g = 240;
                }

                if (noisy)
                {
                    r += rnd.Next(-6, 7);
                    g += rnd.Next(-6, 7);
                    b += rnd.Next(-6, 7);
                }

                int o = ((y * w) + x) * 4;
                px[o] = Clamp(r);
                px[o + 1] = Clamp(g);
                px[o + 2] = Clamp(b);
                px[o + 3] = alpha ? Clamp(255 * (0.15 + (0.85 * Math.Sin((u + v) * 3.1)))) : (byte)255;
            }
        }

        return px;
    }

    /// <summary>An image with few distinct colors (palette-friendly) and flat areas.</summary>
    public static byte[] MakePalette(int w, int h, int colors, int seed, bool alpha = false)
    {
        var rnd = new Random(seed);
        uint[] pal = new uint[colors];
        for (int i = 0; i < colors; i++)
        {
            pal[i] = (uint)rnd.Next() | (alpha ? 0u : 0xff000000u);
        }

        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = ((x / 7) + (y / 5) + (rnd.Next(10) == 0 ? rnd.Next(colors) : 0)) % colors;
                uint c = pal[idx];
                int o = ((y * w) + x) * 4;
                px[o] = (byte)c;
                px[o + 1] = (byte)(c >> 8);
                px[o + 2] = (byte)(c >> 16);
                px[o + 3] = (byte)(c >> 24);
            }
        }

        return px;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Encodes RGBA with libwebp.</summary>
    public static byte[] SkiaEncode(byte[] rgba, int w, int h, bool lossless, int quality)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        using var ms = new MemoryStream();
        using var wstream = new SKManagedWStream(ms);
        using SKPixmap pm = bmp.PeekPixels();
        var opts = new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, quality);
        if (!pm.Encode(wstream, opts))
        {
            throw new InvalidOperationException("Skia WebP encode failed.");
        }

        wstream.Flush();
        return ms.ToArray();
    }

    /// <summary>Decodes a WebP with libwebp to straight-alpha RGBA.</summary>
    public static byte[] SkiaDecode(byte[] webp, out int w, out int h)
    {
        using SKCodec codec = SKCodec.Create(new MemoryStream(webp));
        w = codec.Info.Width;
        h = codec.Info.Height;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        SKCodecResult r = codec.GetPixels(info, bmp.GetPixels());
        if (r != SKCodecResult.Success)
        {
            throw new InvalidOperationException("Skia WebP decode failed: " + r);
        }

        byte[] data = new byte[w * h * 4];
        Marshal.Copy(bmp.GetPixels(), data, 0, data.Length);
        return data;
    }

    /// <summary>Largest per-channel difference over RGB (and A when both have it).</summary>
    public static int MaxDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }

        return max;
    }

    /// <summary>Loads with OneImage and returns RGBA bytes (opaque images get alpha 255).</summary>
    public static byte[] OurDecode(byte[] webp, out int w, out int h)
    {
        Image img = Image.Load(webp);
        w = img.Width;
        h = img.Height;
        Rgba32[] px = img.ToArray<Rgba32>();
        return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
    }
}
