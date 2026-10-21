using Xunit;

namespace OneImage.Tests.Tone;

/// <summary>Shared test images and double-precision reference math for the tone operations.</summary>
internal static class ToneUtil
{
    public static Rgba32[] Pixels8(int w, int h, int seed = 5, bool opaque = true)
    {
        var rnd = new Random(seed);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            // The first 256 pixels walk every value in each channel so tables are covered completely.
            byte r = i < 256 ? (byte)i : (byte)rnd.Next(256);
            byte g = i < 256 ? (byte)(255 - i) : (byte)rnd.Next(256);
            byte b = i < 256 ? (byte)((i * 37) & 255) : (byte)rnd.Next(256);
            byte a = opaque ? (byte)255 : (byte)rnd.Next(256);
            px[i] = new Rgba32(r, g, b, a);
        }

        return px;
    }

    public static Rgba64[] Pixels16(int w, int h, int seed = 6, bool opaque = true)
    {
        var rnd = new Random(seed);
        var px = new Rgba64[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba64((ushort)rnd.Next(65536), (ushort)rnd.Next(65536), (ushort)rnd.Next(65536), opaque ? (ushort)65535 : (ushort)rnd.Next(65536));
        }

        return px;
    }

    public static Image Make8(int w = 300, int h = 150, bool opaque = true, int seed = 5) => Image.FromPixels<Rgba32>(Pixels8(w, h, seed, opaque), w, h);

    public static Image Make16(int w = 300, int h = 150, bool opaque = true) => Image.FromPixels<Rgba64>(Pixels16(w, h, 6, opaque), w, h);

    public static Image MakeFloat(int w = 300, int h = 150)
    {
        var rnd = new Random(7);
        var px = new RgbaFloat[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new RgbaFloat((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());
        }

        return Image.FromPixels<RgbaFloat>(px, w, h);
    }

    /// <summary>Forces a materialization boundary: the result is backed by a canvas, not by the op graph.</summary>
    public static Image Barrier(Image img) => img.ToCanvas().Snapshot();

    public static double Dec(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double Enc(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public static int Q(double v, int max) => (int)Math.Floor((Clamp01(v) * max) + 0.5);

    /// <summary>Largest absolute difference over all channels, in sample steps.</summary>
    public static int MaxDiff(Rgba32[] a, Rgba32[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int m = 0;
        for (int i = 0; i < a.Length; i++)
        {
            m = Math.Max(m, Math.Abs(a[i].R - b[i].R));
            m = Math.Max(m, Math.Abs(a[i].G - b[i].G));
            m = Math.Max(m, Math.Abs(a[i].B - b[i].B));
            m = Math.Max(m, Math.Abs(a[i].A - b[i].A));
        }

        return m;
    }

    public static int MaxDiff(Rgba64[] a, Rgba64[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int m = 0;
        for (int i = 0; i < a.Length; i++)
        {
            m = Math.Max(m, Math.Abs(a[i].R - b[i].R));
            m = Math.Max(m, Math.Abs(a[i].G - b[i].G));
            m = Math.Max(m, Math.Abs(a[i].B - b[i].B));
            m = Math.Max(m, Math.Abs(a[i].A - b[i].A));
        }

        return m;
    }
}
