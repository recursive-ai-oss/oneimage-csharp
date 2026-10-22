using System;
using System.IO;
using OneImage;

/// <summary>Writes a small set of WebP files made by the library's own encoders (lossy, lossless, alpha, animation) to seed the WebP target.</summary>
internal static class WebPSeeds
{
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        int n = 0;
        foreach ((int w, int h) in new[] { (1, 1), (17, 9), (64, 48), (130, 70) })
        {
            foreach (bool alpha in new[] { false, true })
            {
                Image image = Picture(w, h, alpha, 1);
                foreach (bool lossless in new[] { false, true })
                {
                    foreach (int quality in lossless ? new[] { 60 } : new[] { 20, 90 })
                    {
                        Save(image, Path.Combine(directory, $"seed-{w}x{h}-{(alpha ? "alpha" : "opaque")}-{(lossless ? "ll" : "lossy")}{quality}.webp"), lossless, quality);
                        n++;
                    }
                }
            }
        }

        // Animation: three frames of different content.
        Image a = Picture(40, 30, true, 2);
        Image b = Picture(40, 30, true, 3);
        Image c = Picture(40, 30, false, 4);
        foreach (bool lossless in new[] { false, true })
        {
            var anim = new Image([a.Frames[0].Node, b.Frames[0].Node, c.ConvertTo(PixelFormat.Rgba8).Frames[0].Node], [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(40)], 0, a.Metadata, null);
            Save(anim, Path.Combine(directory, $"seed-anim-{(lossless ? "ll" : "lossy")}.webp"), lossless, 70);
        }

        Console.WriteLine($"wrote {n + 2} seeds");
    }

    private static void Save(Image image, string path, bool lossless, int quality)
    {
        image.Save(path, new SaveOptions { Format = new WebPFormat(), EncoderOptions = new WebPEncoderOptions { Lossless = lossless, Quality = quality } });
    }

    private static Image Picture(int w, int h, bool alpha, int seed)
    {
        var rnd = new Random(seed);
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Smooth gradients, a few hard edges and some noise: enough structure for every prediction mode to appear.
                byte r = (byte)((x * 255 / Math.Max(1, w - 1)) ^ (((x / 8) + (y / 8)) % 2 == 0 ? 0 : 0x30));
                byte g = (byte)(y * 255 / Math.Max(1, h - 1));
                byte bl = (byte)(((x + y) * 3) + rnd.Next(12));
                byte al = alpha ? (byte)(x * 255 / Math.Max(1, w - 1)) : (byte)255;
                px[(y * w) + x] = new Rgba32(r, g, bl, al);
            }
        }

        return Image.FromPixels<Rgba32>(px, w, h);
    }
}
