using System.Buffers.Binary;

namespace OneImage.Tests.Png;

/// <summary>Small deterministic image generators and PNG file inspection helpers for the tests.</summary>
internal static class PngGen
{
    public static byte[] Noise(int w, int h, PixelFormat f, int seed)
    {
        var rnd = new Random(seed);
        byte[] b = new byte[w * h * f.BytesPerPixel];
        rnd.NextBytes(b);
        return b;
    }

    /// <summary>Smooth gradients with a little noise: compresses like a photo.</summary>
    public static byte[] Photo(int w, int h, PixelFormat f, int seed)
    {
        var rnd = new Random(seed);
        int ch = f.ChannelCount;
        bool wide = f.Sample == SampleType.U16;
        byte[] b = new byte[w * h * f.BytesPerPixel];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < ch; c++)
                {
                    int v = ((x * (c + 1) / 3) + (y * 2) + (int)(40 * Math.Sin((x + (c * 17)) / 23.0)) + rnd.Next(-3, 4)) & 0xFF;
                    int o = ((y * w) + x) * ch + c;
                    if (wide)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o * 2, 2), (ushort)((v << 8) | rnd.Next(256)));
                    }
                    else
                    {
                        b[o] = (byte)v;
                    }
                }
            }
        }

        return b;
    }

    /// <summary>Flat colors, text-like strokes: compresses like a screenshot.</summary>
    public static byte[] Screenshot(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        byte[] b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                bool panel = (x / 200 + y / 150) % 2 == 0;
                byte bg = panel ? (byte)245 : (byte)32;
                b[o] = bg;
                b[o + 1] = bg;
                b[o + 2] = (byte)(bg ^ 8);
                b[o + 3] = 255;
            }
        }

        for (int t = 0; t < w * h / 300; t++)
        {
            int x0 = rnd.Next(w - 8);
            int y0 = rnd.Next(h - 12);
            byte ink = (byte)rnd.Next(2) == 0 ? (byte)10 : (byte)230;
            for (int dy = 0; dy < 10; dy++)
            {
                for (int dx = 0; dx < 6; dx++)
                {
                    if (((dx * 7) + (dy * 3) + t) % 4 < 2)
                    {
                        int o = (((y0 + dy) * w) + x0 + dx) * 4;
                        b[o] = ink;
                        b[o + 1] = ink;
                        b[o + 2] = ink;
                    }
                }
            }
        }

        return b;
    }

    public static IEnumerable<(string Name, int Offset, int Length, int Data)> Chunks(byte[] png)
    {
        int p = 8;
        while (p + 12 <= png.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            yield return (type, p, (int)len, p + 8);
            p += 12 + (int)len;
        }
    }

    public static (int ColorType, int BitDepth) Header(byte[] png) => (png[25], png[24]);

    public static bool Has(byte[] png, string type) => Chunks(png).Any(c => c.Name == type);

    public static PixelFormat[] AllFormats { get; } =
    [
        PixelFormat.Gray8, PixelFormat.GrayAlpha8, PixelFormat.Gray16, PixelFormat.Rgb8, PixelFormat.Rgba8, PixelFormat.Rgb16, PixelFormat.Rgba16,
    ];

    public static Image Animation(Image[] frames, int[] delaysMs, int loop, ImageMetadata? meta = null)
    {
        Node[] nodes = frames.Select(f => f.Frames[0].Node).ToArray();
        TimeSpan[] delays = delaysMs.Select(d => TimeSpan.FromMilliseconds(d)).ToArray();
        return new Image(nodes, delays, loop, meta ?? ImageMetadata.Empty, null);
    }
}
