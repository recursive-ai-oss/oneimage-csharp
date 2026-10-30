using System.Buffers;
using System.Runtime.CompilerServices;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// Converts PDF colors to 8-bit sRGB with OneImage's color engine. DeviceGray and DeviceRGB are taken as sRGB, as viewers do;
/// DeviceCMYK goes through the generic CMYK profile; ICCBased spaces through their profile (sRGB profiles are recognised and left alone);
/// CalGray, CalRGB and Lab through CIE XYZ with a Bradford adaptation of their white point to D65.
/// </summary>
internal static class PdfColors
{
    private const int Chunk = 512;

    private sealed class IccEntry(ColorTransform? transform, bool identity, int components)
    {
        public ColorTransform? Transform { get; } = transform;

        public bool Identity { get; } = identity;

        public int Components { get; } = components;
    }

    private static readonly ConditionalWeakTable<PdfColorSpace, IccEntry> IccCache = new();
    private static readonly ColorTransform CmykToSrgb = ColorTransform.Default(ColorModel.Cmyk, ColorModel.Rgb);

    public static Rgba32 ToRgba(in PdfColor c) => ToRgba(c.Space, c.C0, c.C1, c.C2, c.C3);

    public static Rgba32 ToRgba(PdfColorSpace space, double c0, double c1, double c2, double c3)
    {
        Span<double> comps = [c0, c1, c2, c3];
        return ToRgba(space, comps);
    }

    public static Rgba32 ToRgba(PdfColorSpace space, ReadOnlySpan<double> comps)
    {
        if (!space.IsFinal)
        {
            Span<double> final = stackalloc double[8];
            if (space.Kind == PdfColorSpaceKind.Pattern)
            {
                return new Rgba32(0, 0, 0, 255);
            }

            space.ToFinal(comps, final);
            return ToRgba(space.FinalSpace, final[..space.FinalSpace.ComponentCount]);
        }

        switch (space.Kind)
        {
            case PdfColorSpaceKind.DeviceGray:
                {
                    byte g = Byte(comps.Length > 0 ? comps[0] : 0);
                    return new Rgba32(g, g, g, 255);
                }

            case PdfColorSpaceKind.DeviceRgb:
                return new Rgba32(Byte(At(comps, 0)), Byte(At(comps, 1)), Byte(At(comps, 2)), 255);
            case PdfColorSpaceKind.DeviceCmyk:
                return Cmyk(At(comps, 0), At(comps, 1), At(comps, 2), At(comps, 3));
            case PdfColorSpaceKind.CalGray:
                {
                    double y = DeterministicMath.Pow(Math.Clamp(At(comps, 0), 0, 1), space.Gamma is { Count: > 0 } gm ? gm[0] : 1);
                    IReadOnlyList<double> wp = space.WhitePoint ?? [0.9505, 1, 1.089];
                    return FromXyz(wp[0] * y, wp[1] * y, wp[2] * y, wp);
                }

            case PdfColorSpaceKind.CalRgb:
                return CalRgb(space, At(comps, 0), At(comps, 1), At(comps, 2));
            case PdfColorSpaceKind.Lab:
                return Lab(space, At(comps, 0), At(comps, 1), At(comps, 2));
            case PdfColorSpaceKind.IccBased:
                {
                    IccEntry e = Icc(space);
                    if (e.Transform is null || e.Identity)
                    {
                        return e.Components switch
                        {
                            1 => ToRgba(PdfColorSpace.Gray, comps),
                            3 => ToRgba(PdfColorSpace.Rgb, comps),
                            _ => ToRgba(PdfColorSpace.Cmyk, comps),
                        };
                    }

                    Span<float> src = stackalloc float[4];
                    Span<float> dst = stackalloc float[4];
                    for (int i = 0; i < e.Components; i++)
                    {
                        src[i] = (float)Math.Clamp(At(comps, i), 0, 1);
                    }

                    e.Transform.ConvertExact(src, dst, 1);
                    return new Rgba32(Byte(dst[0]), Byte(dst[1]), Byte(dst[2]), 255);
                }

            default:
                return new Rgba32(0, 0, 0, 255);
        }
    }

    /// <summary>The luminosity of a color (Rec. 601 weights, as PDF viewers use for luminosity masks), 0 to 255.</summary>
    public static int Luma(Rgba32 c) => ((c.R * 30) + (c.G * 59) + (c.B * 11) + 50) / 100;

    /// <summary>
    /// Converts <paramref name="count"/> pixels of 8-bit samples (<c>space.ComponentCount</c> per pixel, final spaces only) to RGBA with
    /// full alpha. Sample 0..255 maps onto each component's range.
    /// </summary>
    public static void ConvertRow(PdfColorSpace space, ReadOnlySpan<byte> samples, Span<Rgba32> dst, int count)
    {
        switch (space.Kind)
        {
            case PdfColorSpaceKind.DeviceGray:
                for (int i = 0; i < count; i++)
                {
                    byte g = samples[i];
                    dst[i] = new Rgba32(g, g, g, 255);
                }

                return;
            case PdfColorSpaceKind.DeviceRgb:
                RgbRow(samples, dst, count);
                return;
            case PdfColorSpaceKind.DeviceCmyk:
                TransformRow(CmykToSrgb, 4, samples, dst, count);
                return;
            case PdfColorSpaceKind.IccBased:
                {
                    IccEntry e = Icc(space);
                    if (e.Transform is null || e.Identity)
                    {
                        switch (e.Components)
                        {
                            case 1:
                                ConvertRow(PdfColorSpace.Gray, samples, dst, count);
                                return;
                            case 3:
                                RgbRow(samples, dst, count);
                                return;
                            default:
                                TransformRow(CmykToSrgb, 4, samples, dst, count);
                                return;
                        }
                    }

                    TransformRow(e.Transform, e.Components, samples, dst, count);
                    return;
                }

            default:
                {
                    int n = space.ComponentCount;
                    Span<double> comps = stackalloc double[8];
                    for (int i = 0; i < count; i++)
                    {
                        for (int k = 0; k < n; k++)
                        {
                            (double lo, double hi) = space.GetComponentRange(k);
                            comps[k] = lo + (samples[(i * n) + k] * (hi - lo) / 255.0);
                        }

                        dst[i] = ToRgba(space, comps[..n]);
                    }

                    return;
                }
        }
    }

    private static void RgbRow(ReadOnlySpan<byte> samples, Span<Rgba32> dst, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            dst[i] = new Rgba32(samples[o], samples[o + 1], samples[o + 2], 255);
        }
    }

    private static void TransformRow(ColorTransform t, int n, ReadOnlySpan<byte> samples, Span<Rgba32> dst, int count)
    {
        float[] src = Pools<float>.Pool.Rent(Chunk * n);
        float[] outp = Pools<float>.Pool.Rent(Chunk * 3);
        try
        {
            for (int start = 0; start < count; start += Chunk)
            {
                int m = Math.Min(Chunk, count - start);
                ReadOnlySpan<byte> s = samples.Slice(start * n, m * n);
                for (int i = 0; i < m * n; i++)
                {
                    src[i] = s[i] * (1f / 255f);
                }

                t.ConvertFast(src.AsSpan(0, m * n), outp.AsSpan(0, m * 3), m);
                for (int i = 0; i < m; i++)
                {
                    dst[start + i] = new Rgba32(Byte(outp[i * 3]), Byte(outp[(i * 3) + 1]), Byte(outp[(i * 3) + 2]), 255);
                }
            }
        }
        finally
        {
            Pools<float>.Pool.Return(src);
            Pools<float>.Pool.Return(outp);
        }
    }

    private static Rgba32 Cmyk(double c, double m, double y, double k)
    {
        Span<float> src = [(float)Math.Clamp(c, 0, 1), (float)Math.Clamp(m, 0, 1), (float)Math.Clamp(y, 0, 1), (float)Math.Clamp(k, 0, 1)];
        Span<float> dst = stackalloc float[3];
        CmykToSrgb.ConvertExact(src, dst, 1);
        return new Rgba32(Byte(dst[0]), Byte(dst[1]), Byte(dst[2]), 255);
    }

    private static IccEntry Icc(PdfColorSpace space) =>
        IccCache.GetValue(space, static s =>
        {
            int n = s.ComponentCount;
            if (n is not (1 or 3 or 4) || s.IccProfile.IsEmpty || !ColorProfile.TryFromIcc(s.IccProfile.Span, out ColorProfile? profile) || profile is null)
            {
                return new IccEntry(null, true, n is 1 or 3 or 4 ? n : 3);
            }

            int channels = profile.Model switch { ColorModel.Gray => 1, ColorModel.Cmyk => 4, ColorModel.Lab => 3, _ => 3 };
            if (channels != n || profile.Model == ColorModel.Lab)
            {
                return new IccEntry(null, true, n);
            }

            // An sRGB profile (there are many byte-different ones) changes nothing: skipping the transform keeps the common case fast and exact.
            if (n == 3 && profile.Description.Contains("sRGB", StringComparison.OrdinalIgnoreCase))
            {
                return new IccEntry(null, true, n);
            }

            try
            {
                ColorProfile dest = ColorProfile.Srgb;
                return new IccEntry(ColorTransform.Get(profile, dest, RenderingIntent.RelativeColorimetric, true), false, n);
            }
            catch (ImageException)
            {
                // A profile the engine cannot build a transform from is replaced by the device space, as the PDF specification allows.
                return new IccEntry(null, true, n);
            }
        });

    private static Rgba32 CalRgb(PdfColorSpace space, double a, double b, double c)
    {
        IReadOnlyList<double> gamma = space.Gamma is { Count: >= 3 } g ? g : [1, 1, 1];
        IReadOnlyList<double> m = space.Matrix is { Count: >= 9 } mm ? mm : [1, 0, 0, 0, 1, 0, 0, 0, 1];
        IReadOnlyList<double> wp = space.WhitePoint ?? [0.9505, 1, 1.089];
        double ag = DeterministicMath.Pow(Math.Clamp(a, 0, 1), gamma[0]);
        double bg = DeterministicMath.Pow(Math.Clamp(b, 0, 1), gamma[1]);
        double cg = DeterministicMath.Pow(Math.Clamp(c, 0, 1), gamma[2]);
        double x = (m[0] * ag) + (m[3] * bg) + (m[6] * cg);
        double y = (m[1] * ag) + (m[4] * bg) + (m[7] * cg);
        double z = (m[2] * ag) + (m[5] * bg) + (m[8] * cg);
        return FromXyz(x, y, z, wp);
    }

    private static Rgba32 Lab(PdfColorSpace space, double l, double a, double b)
    {
        IReadOnlyList<double> wp = space.WhitePoint ?? [0.9642, 1, 0.8249];
        double fy = (Math.Clamp(l, 0, 100) + 16) / 116;
        double fx = fy + (a / 500);
        double fz = fy - (b / 200);
        static double G(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - (4.0 / 29));
        return FromXyz(wp[0] * G(fx), wp[1] * G(fy), wp[2] * G(fz), wp);
    }

    // Bradford cone-response adaptation from the space's white point to D65, then the sRGB matrix and transfer curve.
    private static Rgba32 FromXyz(double x, double y, double z, IReadOnlyList<double> wp)
    {
        const double Xd = 0.95047, Yd = 1.0, Zd = 1.08883;
        double ws = (0.8951 * wp[0]) + (0.2664 * wp[1]) - (0.1614 * wp[2]);
        double wy = (-0.7502 * wp[0]) + (1.7135 * wp[1]) + (0.0367 * wp[2]);
        double wz = (0.0389 * wp[0]) - (0.0685 * wp[1]) + (1.0296 * wp[2]);
        double ds = (0.8951 * Xd) + (0.2664 * Yd) - (0.1614 * Zd);
        double dy = (-0.7502 * Xd) + (1.7135 * Yd) + (0.0367 * Zd);
        double dz = (0.0389 * Xd) - (0.0685 * Yd) + (1.0296 * Zd);
        double s = (0.8951 * x) + (0.2664 * y) - (0.1614 * z);
        double t = (-0.7502 * x) + (1.7135 * y) + (0.0367 * z);
        double u = (0.0389 * x) - (0.0685 * y) + (1.0296 * z);
        s *= ws > 1e-9 ? ds / ws : 1;
        t *= Math.Abs(wy) > 1e-9 ? dy / wy : 1;
        u *= Math.Abs(wz) > 1e-9 ? dz / wz : 1;
        double xa = (0.9869929 * s) - (0.1470543 * t) + (0.1599627 * u);
        double ya = (0.4323053 * s) + (0.5183603 * t) + (0.0492912 * u);
        double za = (-0.0085287 * s) + (0.0400428 * t) + (0.9684867 * u);
        double r = (3.2404542 * xa) - (1.5371385 * ya) - (0.4985314 * za);
        double g = (-0.9692660 * xa) + (1.8760108 * ya) + (0.0415560 * za);
        double bl = (0.0556434 * xa) - (0.2040259 * ya) + (1.0572252 * za);
        return new Rgba32(Encode(r), Encode(g), Encode(bl), 255);
    }

    private static byte Encode(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double e = v <= 0.0031308 ? 12.92 * v : (1.055 * DeterministicMath.Pow(v, 1 / 2.4)) - 0.055;
        return Byte(e);
    }

    private static double At(ReadOnlySpan<double> s, int i) => i < s.Length ? s[i] : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Byte(double v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)((v * 255) + 0.5);
}
