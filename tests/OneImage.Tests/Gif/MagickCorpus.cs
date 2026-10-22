using ImageMagick;

namespace OneImage.Tests.Gif;

/// <summary>Deterministic GIF files written by Magick.NET, used as the reference for the decoder.</summary>
public static class MagickCorpus
{
    /// <summary>One frame: pixels (RGBA, w*h*4), where it sits on the canvas, and how it is disposed.</summary>
    public sealed record Part(int X, int Y, int W, int H, byte[] Rgba, GifDisposeMethod Dispose, uint DelayCs);

    public static byte[] Pattern(int w, int h, int seed, int colors, bool holes)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(seed);
        var pal = new (byte R, byte G, byte B)[colors];
        for (int i = 0; i < colors; i++)
        {
            pal[i] = ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int k = (((x / 5) + (y / 3)) * 7 + seed) % colors;
                (byte r, byte g, byte b) = pal[k];
                int o = ((y * w) + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = holes && (((x / 4) + (y / 4)) % 3 == 0) ? (byte)0 : (byte)255;
            }
        }

        return rgba;
    }

    public static byte[] Write(int canvasW, int canvasH, IReadOnlyList<Part> parts, uint? iterations, bool interlace = false)
    {
        using var coll = new MagickImageCollection();
        foreach (Part p in parts)
        {
            var img = new MagickImage(MagickColors.Transparent, (uint)p.W, (uint)p.H);
            img.ImportPixels(p.Rgba, new PixelImportSettings((uint)p.W, (uint)p.H, StorageType.Char, PixelMapping.RGBA));
            img.Page = new MagickGeometry(p.X, p.Y, (uint)canvasW, (uint)canvasH);
            img.GifDisposeMethod = p.Dispose;
            img.AnimationDelay = p.DelayCs;
            if (iterations is { } it)
            {
                img.AnimationIterations = it;
            }

            if (interlace)
            {
                img.Settings.Interlace = Interlace.Gif;
            }

            coll.Add(img);
        }

        using var ms = new MemoryStream();
        coll.Write(ms, MagickFormat.Gif);
        return ms.ToArray();
    }

    /// <summary>Reads a GIF with Magick.NET and returns its coalesced frames (RGBA), delays (cs) and iterations.</summary>
    public static (List<byte[]> Frames, List<uint> Delays, uint Iterations, int W, int H) Reference(byte[] gif)
    {
        using var coll = new MagickImageCollection(gif);
        coll.Coalesce();
        var frames = new List<byte[]>();
        var delays = new List<uint>();
        foreach (IMagickImage<byte> f in coll)
        {
            frames.Add(f.GetPixels().ToByteArray(PixelMapping.RGBA)!);
            delays.Add(f.AnimationDelay);
        }

        return (frames, delays, coll[0].AnimationIterations, (int)coll[0].Width, (int)coll[0].Height);
    }

    /// <summary>Makes pixels with alpha 0 compare equal whatever their color.</summary>
    public static byte[] Normalize(byte[] rgba)
    {
        var c = (byte[])rgba.Clone();
        for (int i = 0; i < c.Length; i += 4)
        {
            if (c[i + 3] == 0)
            {
                c[i] = c[i + 1] = c[i + 2] = 0;
            }
        }

        return c;
    }
}
