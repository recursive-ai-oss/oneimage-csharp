using System.Buffers.Binary;
using OneImage.Tests.Png;

namespace OneImage.Tests;

/// <summary>
/// Builders for the short-read tests: PNG files of chosen size, layout and IDAT chunking, and helpers that decode them
/// through readers that return fewer bytes than asked.
/// </summary>
internal static class ShortReadFixtures
{
    /// <summary>Rewrites the file so its compressed data is split into IDAT chunks of at most <paramref name="idatSize"/> bytes.</summary>
    public static byte[] Rechunk(byte[] png, int idatSize)
    {
        var chunks = PngGen.Chunks(png).ToList();
        using var data = new MemoryStream();
        foreach (var c in chunks.Where(c => c.Name == "IDAT"))
        {
            data.Write(png, c.Data, c.Length);
        }

        byte[] z = data.ToArray();
        using var o = new MemoryStream();
        o.Write(png, 0, 8);
        bool wrote = false;
        foreach (var c in chunks)
        {
            if (c.Name == "IDAT")
            {
                if (!wrote)
                {
                    wrote = true;
                    for (int p = 0; p < z.Length; p += idatSize)
                    {
                        o.Write(MiniPng.Chunk("IDAT", z.AsSpan(p, Math.Min(idatSize, z.Length - p))));
                    }
                }

                continue;
            }

            o.Write(png, c.Offset, 12 + c.Length);
        }

        return o.ToArray();
    }

    /// <summary>An 8- or 16-bit PNG written by the library (adaptive filters), re-split into IDAT chunks of <paramref name="idatSize"/> bytes.</summary>
    public static byte[] Png(int w, int h, PixelFormat f, int idatSize, int seed = 1)
    {
        byte[] pixels = PngGen.Photo(w, h, f, seed);
        return Rechunk(PngTestUtil.Encode(pixels, w, h, f), idatSize);
    }

    /// <summary>An Adam7-interlaced 8-bit RGBA PNG (filter 0), split into IDAT chunks of <paramref name="idatSize"/> bytes.</summary>
    public static byte[] InterlacedPng(int w, int h, int idatSize, int seed = 1)
    {
        int[] xo = [0, 4, 0, 2, 0, 1, 0];
        int[] yo = [0, 0, 4, 0, 2, 0, 1];
        int[] dx = [8, 8, 4, 4, 2, 2, 1];
        int[] dy = [8, 8, 8, 4, 4, 2, 2];
        byte[] px = PngGen.Photo(w, h, PixelFormat.Rgba8, seed);
        using var raw = new MemoryStream();
        for (int p = 0; p < 7; p++)
        {
            for (int y = yo[p]; y < h; y += dy[p])
            {
                if (xo[p] >= w)
                {
                    continue;
                }

                raw.WriteByte(0);
                for (int x = xo[p]; x < w; x += dx[p])
                {
                    raw.Write(px, ((y * w) + x) * 4, 4);
                }
            }
        }

        byte[] z = MiniPng.Zlib(raw.ToArray());
        using var o = new MemoryStream();
        o.Write(MiniPng.Signature);
        o.Write(MiniPng.Chunk("IHDR", MiniPng.Ihdr(w, h, 8, 6, 1)));
        for (int p = 0; p < z.Length; p += idatSize)
        {
            o.Write(MiniPng.Chunk("IDAT", z.AsSpan(p, Math.Min(idatSize, z.Length - p))));
        }

        o.Write(MiniPng.Chunk("IEND", []));
        return o.ToArray();
    }

    /// <summary>Decodes every pixel as Rgba64 (a lossless view of every 8- and 16-bit layout).</summary>
    public static Rgba64[] All(Image img) => img.ToArray<Rgba64>();
}
