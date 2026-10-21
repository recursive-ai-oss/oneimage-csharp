using ImageMagick;

namespace OneImage.Tests;

/// <summary>Shared helpers for color tests: a LittleCMS reference (through Magick.NET) and a CIEDE2000 metric.</summary>
internal static class ColorTestUtil
{
    public static string TestDataPath(string relative)
    {
        string p = Path.Combine(AppContext.BaseDirectory, "testdata", relative);
        return p;
    }

    /// <summary>Converts packed 8-bit pixels (3 or 4 channels) from <paramref name="src"/> to <paramref name="dst"/> with LittleCMS.</summary>
    public static byte[] Lcms(byte[] pixels, int width, int height, ColorModel srcModel, ColorModel dstModel, byte[] src, byte[] dst, ImageMagick.RenderingIntent intent, bool bpc)
    {
        string map = srcModel == ColorModel.Cmyk ? "CMYK" : srcModel == ColorModel.Gray ? "I" : "RGB";
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, map);
        using var img = new MagickImage(pixels, settings);
        if (srcModel == ColorModel.Cmyk)
        {
            img.ColorSpace = ColorSpace.CMYK;
        }
        else if (srcModel == ColorModel.Gray)
        {
            img.ColorSpace = ColorSpace.Gray;
        }
        else
        {
            img.ColorSpace = ColorSpace.sRGB;
        }

        img.RenderingIntent = intent;
        img.BlackPointCompensation = bpc;
        img.TransformColorSpace(new ImageMagick.ColorProfile(src), new ImageMagick.ColorProfile(dst));
        string outMap = dstModel == ColorModel.Cmyk ? "CMYK" : dstModel == ColorModel.Gray ? "I" : "RGB";
        return img.GetPixels().ToByteArray(outMap)!;
    }

    public static ImageMagick.RenderingIntent ToMagick(OneImage.RenderingIntent i) => i switch
    {
        OneImage.RenderingIntent.Perceptual => ImageMagick.RenderingIntent.Perceptual,
        OneImage.RenderingIntent.Saturation => ImageMagick.RenderingIntent.Saturation,
        OneImage.RenderingIntent.AbsoluteColorimetric => ImageMagick.RenderingIntent.Absolute,
        _ => ImageMagick.RenderingIntent.Relative,
    };

    /// <summary>Converts 8-bit device pixels of <paramref name="profile"/> to Lab through our exact pipeline.</summary>
    public static float[] ToLab(byte[] pixels, int channels, ColorProfile profile)
    {
        int n = pixels.Length / channels;
        var f = new float[pixels.Length];
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = pixels[i] / 255f;
        }

        var lab = new float[n * 3];
        ColorTransform.Get(profile, ColorProfile.LabD50, OneImage.RenderingIntent.RelativeColorimetric, false).ConvertExact(f, lab, n);
        return lab;
    }

    public static double DeltaE2000(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        DeltaE2000(a[0], a[1], a[2], b[0], b[1], b[2]);

    public static double DeltaE2000(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        const double Rad = Math.PI / 180;
        double c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        double c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        double cb = (c1 + c2) / 2;
        double cb7 = Math.Pow(cb, 7);
        double g = 0.5 * (1 - Math.Sqrt(cb7 / (cb7 + Math.Pow(25, 7))));
        double ap1 = (1 + g) * a1, ap2 = (1 + g) * a2;
        double cp1 = Math.Sqrt((ap1 * ap1) + (b1 * b1)), cp2 = Math.Sqrt((ap2 * ap2) + (b2 * b2));
        double hp1 = (ap1 == 0 && b1 == 0) ? 0 : Math.Atan2(b1, ap1) / Rad;
        double hp2 = (ap2 == 0 && b2 == 0) ? 0 : Math.Atan2(b2, ap2) / Rad;
        if (hp1 < 0)
        {
            hp1 += 360;
        }

        if (hp2 < 0)
        {
            hp2 += 360;
        }

        double dl = l2 - l1;
        double dc = cp2 - cp1;
        double dh;
        if (cp1 * cp2 == 0)
        {
            dh = 0;
        }
        else if (Math.Abs(hp2 - hp1) <= 180)
        {
            dh = hp2 - hp1;
        }
        else if (hp2 - hp1 > 180)
        {
            dh = hp2 - hp1 - 360;
        }
        else
        {
            dh = hp2 - hp1 + 360;
        }

        double dH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(dh * Rad / 2);
        double lb = (l1 + l2) / 2;
        double cpb = (cp1 + cp2) / 2;
        double hb;
        if (cp1 * cp2 == 0)
        {
            hb = hp1 + hp2;
        }
        else if (Math.Abs(hp1 - hp2) <= 180)
        {
            hb = (hp1 + hp2) / 2;
        }
        else if (hp1 + hp2 < 360)
        {
            hb = (hp1 + hp2 + 360) / 2;
        }
        else
        {
            hb = (hp1 + hp2 - 360) / 2;
        }

        double t = 1 - (0.17 * Math.Cos((hb - 30) * Rad)) + (0.24 * Math.Cos(2 * hb * Rad)) + (0.32 * Math.Cos(((3 * hb) + 6) * Rad)) - (0.20 * Math.Cos(((4 * hb) - 63) * Rad));
        double dTheta = 30 * Math.Exp(-Math.Pow((hb - 275) / 25, 2));
        double rc = 2 * Math.Sqrt(Math.Pow(cpb, 7) / (Math.Pow(cpb, 7) + Math.Pow(25, 7)));
        double sl = 1 + (0.015 * Math.Pow(lb - 50, 2) / Math.Sqrt(20 + Math.Pow(lb - 50, 2)));
        double sc = 1 + (0.045 * cpb);
        double sh = 1 + (0.015 * cpb * t);
        double rt = -Math.Sin(2 * dTheta * Rad) * rc;
        return Math.Sqrt(Math.Pow(dl / sl, 2) + Math.Pow(dc / sc, 2) + Math.Pow(dH / sh, 2) + (rt * (dc / sc) * (dH / sh)));
    }

    /// <summary>A grid of 8-bit device values: every corner/edge-heavy lattice point plus seeded random points.</summary>
    public static byte[] Grid(int channels, int steps, int random, int seed)
    {
        var list = new List<byte>();
        int[] levels = Enumerable.Range(0, steps).Select(i => i * 255 / (steps - 1)).ToArray();
        void Rec(int depth, byte[] cur)
        {
            if (depth == channels)
            {
                list.AddRange(cur);
                return;
            }

            foreach (int l in levels)
            {
                cur[depth] = (byte)l;
                Rec(depth + 1, cur);
            }
        }

        Rec(0, new byte[channels]);
        var rnd = new Random(seed);
        byte[] r = new byte[random * channels];
        rnd.NextBytes(r);
        list.AddRange(r);
        return list.ToArray();
    }
}
