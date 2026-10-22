using System.IO;
using System.Linq;
using ImageMagick;
using SixLabors.ImageSharp.Processing;
using IS = SixLabors.ImageSharp;
using Rgba32Is = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Benchmarks.Gif;

/// <summary>Deterministic inputs shared by the GIF and quantizer benchmarks.</summary>
internal static class GifBenchData
{
    /// <summary>A ~50-frame 500x500 animated GIF in the usual optimized form: partial frames over a shared background.</summary>
    public static byte[] Animation(int frames = 50, int size = 500)
    {
        var rng = new Random(7);
        var bg = new byte[size * size * 4];
        var pal = new byte[120 * 3];
        rng.NextBytes(pal);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int k = (((x / 9) ^ (y / 7)) + (x * y / 4000)) % 120;
                int o = ((y * size) + x) * 4;
                bg[o] = pal[k * 3];
                bg[o + 1] = pal[(k * 3) + 1];
                bg[o + 2] = pal[(k * 3) + 2];
                bg[o + 3] = 255;
            }
        }

        using var coll = new MagickImageCollection();
        for (int i = 0; i < frames; i++)
        {
            byte[] f = (byte[])bg.Clone();
            int sx = 20 + ((i * 17) % (size - 140));
            int sy = 30 + ((i * 11) % (size - 140));
            for (int y = 0; y < 100; y++)
            {
                for (int x = 0; x < 100; x++)
                {
                    int o = (((sy + y) * size) + sx + x) * 4;
                    int c = ((x / 12) + (y / 12) + i) % 8;
                    f[o] = (byte)(c * 30);
                    f[o + 1] = (byte)(255 - (c * 20));
                    f[o + 2] = (byte)(60 + (c * 25));
                }
            }

            var img = new MagickImage(MagickColors.Black, (uint)size, (uint)size);
            img.ImportPixels(f, new PixelImportSettings((uint)size, (uint)size, StorageType.Char, PixelMapping.RGBA));
            img.AnimationDelay = 4;
            img.GifDisposeMethod = GifDisposeMethod.None;
            coll.Add(img);
        }

        coll.Optimize();
        using var ms = new MemoryStream();
        coll.Write(ms, MagickFormat.Gif);
        return ms.ToArray();
    }

    /// <summary>A 2000x1500 photo: a crop of a system wallpaper when one is available (set ONEIMAGE_PHOTO to choose), else a synthetic image.</summary>
    public static byte[] PhotoRgba(out int w, out int h, string? photoPath = null)
    {
        w = 2000;
        h = 1500;
        string? path = photoPath ?? Environment.GetEnvironmentVariable("ONEIMAGE_PHOTO");
        if (path == null && Directory.Exists("/usr/share/wallpapers"))
        {
            path = Directory.EnumerateFiles("/usr/share/wallpapers", "2560x1600.jpg", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        }

        if (path != null && File.Exists(path))
        {
            using IS.Image<Rgba32Is> src = IS.Image.Load<Rgba32Is>(path);
            int cw = Math.Min(2000, src.Width);
            int ch = Math.Min(1500, src.Height);
            src.Mutate(x => x.Crop(new IS.Rectangle(0, 0, cw, ch)));
            if (src.Width == w && src.Height == h)
            {
                var bytes = new byte[w * h * 4];
                src.CopyPixelDataTo(bytes);
                return bytes;
            }
        }

        var rng = new Random(3);
        var raw = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)w, v = y / (double)h;
                int o = ((y * w) + x) * 4;
                raw[o] = (byte)Math.Clamp(128 + (100 * Math.Sin((u * 7) + (v * 3))) + rng.Next(-5, 6), 0, 255);
                raw[o + 1] = (byte)Math.Clamp(128 + (90 * Math.Cos((v * 5) - u)) + rng.Next(-5, 6), 0, 255);
                raw[o + 2] = (byte)Math.Clamp(128 + (110 * Math.Sin((u * v) * 20)) + rng.Next(-5, 6), 0, 255);
                raw[o + 3] = 255;
            }
        }

        return raw;
    }
}
