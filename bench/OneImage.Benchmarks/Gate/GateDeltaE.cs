using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ImageMagick;
using SkiaSharp;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgb = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>;
using VipsImage = NetVips.Image;
using OI = global::OneImage;

namespace OneImage.Benchmarks.Gate;

/// <summary>
/// Colour accuracy of the CMYK JPEG to sRGB task: <c>gate-deltae</c> runs every library's path on the gate input and compares its
/// sRGB output with an ICC-correct reference, as CIEDE2000 over a deterministic sample of the pixels (every 11th column of every 7th row).
/// The reference converts our decoder's CMYK samples through the embedded profile with this library's exact-math (floating point,
/// no lookup grid) conversion, relative colorimetric with black point compensation. Prints one <c>DELTAE</c> line per library:
/// mean and maximum.
/// </summary>
internal static class GateDeltaE
{
    private const int StepX = 11;
    private const int StepY = 7;

    public static int Run()
    {
        var b = new CmykJpegGateBenchmarks();
        b.Setup();
        byte[] jpeg = b.Jpeg;
        int w = CmykJpegGateBenchmarks.Width, h = CmykJpegGateBenchmarks.Height;

        // Reference: our decode of the CMYK samples, converted with exact math.
        OI.Cmyk32[] cmyk = OI.Image.Load(jpeg).ToArray<OI.Cmyk32>();
        OI.ColorProfile source = OI.Image.Load(jpeg).ColorProfile;
        int sx = (w + StepX - 1) / StepX, sy = (h + StepY - 1) / StepY;
        int n = sx * sy;
        var dev = new float[n * 4];
        for (int j = 0, k = 0; j < sy; j++)
        {
            for (int i = 0; i < sx; i++, k++)
            {
                OI.Cmyk32 p = cmyk[(j * StepY * w) + (i * StepX)];
                dev[(4 * k)] = p.C / 255f;
                dev[(4 * k) + 1] = p.M / 255f;
                dev[(4 * k) + 2] = p.Y / 255f;
                dev[(4 * k) + 3] = p.K / 255f;
            }
        }

        cmyk = null!;
        var srgbRef = new float[n * 3];
        OI.ColorTransform.Get(source, OI.ColorProfile.Srgb, OI.RenderingIntent.RelativeColorimetric, true).ConvertExact(dev, srgbRef, n);
        for (int i = 0; i < srgbRef.Length; i++)
        {
            // Outputs are 8-bit and so clipped to the sRGB cube; the reference is clipped the same way.
            srgbRef[i] = Math.Clamp(srgbRef[i], 0f, 1f);
        }

        float[] labRef = ToLab(srgbRef, n);
        var labs = new Dictionary<string, float[]>();

        foreach ((string lib, Func<byte[]?> run) in new (string, Func<byte[]?>)[]
        {
            ("oneimage", () => b.OneImageRgb()),
            ("oneimage-nobpc", () => b.OneImageRgb(blackPointCompensation: false)),
            ("imagesharp", () => b.ImageSharpRgb()),
            ("skia", () => b.SkiaRgb()),
            ("netvips", () => b.NetVipsRgb()),
            ("magick", () => b.MagickRgb()),
        })
        {
            byte[]? rgb = run();
            if (rgb == null)
            {
                continue;
            }

            var sample = new float[n * 3];
            for (int j = 0, k = 0; j < sy; j++)
            {
                for (int i = 0; i < sx; i++, k++)
                {
                    int o = ((j * StepY * w) + (i * StepX)) * 3;
                    sample[3 * k] = rgb[o] / 255f;
                    sample[(3 * k) + 1] = rgb[o + 1] / 255f;
                    sample[(3 * k) + 2] = rgb[o + 2] / 255f;
                }
            }

            labs[lib] = ToLab(sample, n);
        }

        // Second reference: LittleCMS through Magick.NET (what a user of that library would call correct).
        labs.TryGetValue("magick", out float[]? lcms);
        foreach ((string lib, float[] lab) in labs)
        {
            (double mean, double max) = Compare(labRef, lab, n);
            (double lmean, double lmax) = lcms == null ? (double.NaN, double.NaN) : Compare(lcms, lab, n);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DELTAE\tcmyk-jpeg-to-srgb\t{lib}\t{mean:F3}\t{max:F3}\t{n}\t{lmean:F3}\t{lmax:F3}"));
        }

        return 0;
    }

    private static (double Mean, double Max) Compare(float[] reference, float[] lab, int n)
    {
        double sum = 0, max = 0;
        for (int k = 0; k < n; k++)
        {
            double d = DeltaE2000(reference[3 * k], reference[(3 * k) + 1], reference[(3 * k) + 2], lab[3 * k], lab[(3 * k) + 1], lab[(3 * k) + 2]);
            sum += d;
            max = Math.Max(max, d);
        }

        return (sum / n, max);
    }

    private static float[] ToLab(float[] srgb, int n)
    {
        var lab = new float[n * 3];
        OI.ColorTransform.Get(OI.ColorProfile.Srgb, OI.ColorProfile.LabD50, OI.RenderingIntent.RelativeColorimetric, false).ConvertExact(srgb, lab, n);
        return lab;
    }

    /// <summary>CIEDE2000 (Sharma, Wu and Dalal, 2005).</summary>
    private static double DeltaE2000(double l1, double a1, double b1, double l2, double a2, double b2)
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
}
