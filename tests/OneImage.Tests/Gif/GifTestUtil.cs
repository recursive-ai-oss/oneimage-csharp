namespace OneImage.Tests.Gif;

internal static class GifTestUtil
{
    public static Image Animation(int w, int h, IReadOnlyList<Rgba32[]> frames, int delayMs = 100, int loop = 0, ImageMetadata? metadata = null)
    {
        var nodes = new Node[frames.Count];
        var delays = new TimeSpan[frames.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = Image.FromPixels<Rgba32>(frames[i], w, h).Frames[0].Node;
            delays[i] = TimeSpan.FromMilliseconds(delayMs + (i * 10));
        }

        return new Image(nodes, delays, loop, metadata ?? ImageMetadata.Empty, null);
    }

    public static byte[] Encode(Image image, GifEncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, GifFormat.Instance, new SaveOptions { EncoderOptions = options });
        return ms.ToArray();
    }

    public static Image Decode(byte[] gif) =>
        Image.Load(gif, new LoadOptions { Configuration = new ImageConfiguration().Add(GifFormat.Instance), Validate = true });

    public static Rgba32[] Pixels(Image img, int frame = 0) => img.Frames[frame].ToArray<Rgba32>();

    /// <summary>Colors of transparent pixels do not matter: alpha below 128 compares as one value.</summary>
    public static bool SameVisible(Rgba32[] a, Rgba32[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            bool ta = a[i].A < 128;
            bool tb = b[i].A < 128;
            if (ta != tb || (!ta && (a[i].R != b[i].R || a[i].G != b[i].G || a[i].B != b[i].B)))
            {
                return false;
            }
        }

        return true;
    }

    public static Rgba32[] Pattern(int w, int h, int seed, int colors, bool holes)
    {
        byte[] raw = MagickCorpus.Pattern(w, h, seed, colors, holes);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32(raw[i * 4], raw[(i * 4) + 1], raw[(i * 4) + 2], raw[(i * 4) + 3]);
        }

        return px;
    }

    /// <summary>A deterministic photo-like image: smooth gradients, soft shapes and fine noise, many thousands of distinct colors.</summary>
    public static Rgba32[] SyntheticPhoto(int w, int h, int seed = 1)
    {
        var rng = new Random(seed);
        var px = new Rgba32[w * h];
        double p1 = rng.NextDouble() * 6, p2 = rng.NextDouble() * 6, p3 = rng.NextDouble() * 6;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)w, v = y / (double)h;
                double r = 120 + (80 * Math.Sin((u * 5) + p1) * Math.Cos((v * 3) + p2)) + (30 * Math.Sin((u + v) * 20));
                double g = 110 + (70 * Math.Sin((v * 4) + p2) + (40 * Math.Cos((u * 6) + p3))) * 0.8;
                double b = 100 + (90 * Math.Cos(((u * u) + (v * v)) * 6 + p3));
                double d = Math.Sqrt(((u - 0.6) * (u - 0.6)) + ((v - 0.4) * (v - 0.4)));
                if (d < 0.2)
                {
                    r = (r * 0.3) + 200;
                    g *= 0.5;
                }

                r += rng.Next(-6, 7);
                g += rng.Next(-6, 7);
                b += rng.Next(-6, 7);
                px[(y * w) + x] = new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), 255);
            }
        }

        return px;
    }

    public static double Psnr(Rgba32[] a, Rgba32[] b)
    {
        double se = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double dr = a[i].R - b[i].R, dg = a[i].G - b[i].G, db = a[i].B - b[i].B;
            se += (dr * dr) + (dg * dg) + (db * db);
        }

        double mse = se / (a.Length * 3.0);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
