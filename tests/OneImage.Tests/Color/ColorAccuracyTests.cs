namespace OneImage.Tests;

public class ColorAccuracyTests
{
    public static TheoryData<string, string, int, bool> RgbPairs()
    {
        var d = new TheoryData<string, string, int, bool>();
        string[] names = ["srgb", "p3", "wide", "pro", "2020", "lsrgb"];
        foreach (int intent in new[] { 0, 1 })
        {
            foreach (bool bpc in new[] { false, true })
            {
                for (int i = 0; i < names.Length; i++)
                {
                    for (int j = 0; j < names.Length; j++)
                    {
                        if (i != j)
                        {
                            d.Add(names[i], names[j], intent, bpc);
                        }
                    }
                }
            }
        }

        return d;
    }

    internal static ColorProfile Named(string n) => n switch
    {
        "srgb" => ColorProfile.Srgb,
        "p3" => ColorProfile.DisplayP3,
        "wide" => ColorProfile.WideGamutRgb,
        "pro" => ColorProfile.ProPhoto,
        "2020" => ColorProfile.Rec2020,
        "lsrgb" => ColorProfile.LinearSrgb,
        "srgb2014" => ColorProfile.FromIcc(File.ReadAllBytes(ColorTestUtil.TestDataPath("icc/sRGB2014.icc"))),
        "srgb4" => ColorProfile.FromIcc(File.ReadAllBytes(ColorTestUtil.TestDataPath("icc/sRGB_v4_ICC_preference.icc"))),
        "cmyk" => ColorProfile.GenericCmyk,
        "gray" => ColorProfile.Gray,
        _ when n.StartsWith('/') => ColorProfile.FromIcc(File.ReadAllBytes(n)),
        _ => throw new ArgumentException(n),
    };

    /// <summary>Compares our 8-bit conversion with LittleCMS over a lattice plus random colors; returns (max dE00, mean dE00, max LSB).</summary>
    internal static (double Max, double Mean, int MaxLsb) Compare(ColorProfile src, ColorProfile dst, OneImage.RenderingIntent intent, bool bpc, int seed = 1, float margin = 0.15f)
    {
        int sc = src.Model == ColorModel.Cmyk ? 4 : src.Model == ColorModel.Gray ? 1 : 3;
        int dc = dst.Model == ColorModel.Cmyk ? 4 : dst.Model == ColorModel.Gray ? 1 : 3;
        byte[] px = ColorTestUtil.Grid(sc, sc == 4 ? 6 : sc == 1 ? 256 : 9, sc == 4 ? 2000 : sc == 1 ? 0 : 3000, seed);
        int n = px.Length / sc;

        byte[] theirs = ColorTestUtil.Lcms(px, n, 1, src.Model, dst.Model, src.IccBytes.ToArray(), dst.IccBytes.ToArray(), ColorTestUtil.ToMagick(intent), bpc);

        var fmtIn = new PixelFormat(src.Model, SampleType.U8);
        var fmtOut = new PixelFormat(dst.Model, SampleType.U8);
        ColorTransform t = ColorTransform.Get(src, dst, intent, bpc);
        byte[] ours = new byte[n * dc];
        t.ConvertRow(px, fmtIn, ours, fmtOut, n);

        // Colors the destination cannot reproduce are clipped, and near the gamut surface LittleCMS interpolates between clipped
        // grid nodes (its result there deviates from the exact math by several LSB), so judge colors a margin (15% of the range) inside the gamut; ColorMathTests checks the surface against double-precision math.
        var exactIn = new float[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            exactIn[i] = px[i] / 255f;
        }

        var exactOut = new float[n * dc];
        t.ConvertExact(exactIn, exactOut, n);

        float[] la = ColorTestUtil.ToLab(ours, dc, dst);
        float[] lb = ColorTestUtil.ToLab(theirs, dc, dst);
        int lsb = 0, count = 0;
        double max = 0, sum = 0;
        for (int i = 0; i < n; i++)
        {
            bool inGamut = true;
            for (int c = 0; c < dc; c++)
            {
                inGamut &= exactOut[(i * dc) + c] >= margin && exactOut[(i * dc) + c] <= 1f - margin || dst.Model == ColorModel.Gray;
            }

            if (!inGamut)
            {
                continue;
            }

            count++;
            for (int c = 0; c < dc; c++)
            {
                lsb = Math.Max(lsb, Math.Abs(ours[(i * dc) + c] - theirs[(i * dc) + c]));
            }

            double e = ColorTestUtil.DeltaE2000(la.AsSpan(i * 3, 3), lb.AsSpan(i * 3, 3));
            max = Math.Max(max, e);
            sum += e;
        }

        return (max, sum / Math.Max(1, count), lsb);
    }

    internal static void Report(string label, (double Max, double Mean, int MaxLsb) r)
    {
        string line = $"{label}: max dE00 {r.Max:F3}, mean {r.Mean:F3}, max LSB {r.MaxLsb}";
        TestContext.Current.SendDiagnosticMessage(line);
        string log = Environment.GetEnvironmentVariable("ONEIMAGE_COLOR_LOG") ?? string.Empty;
        if (log.Length > 0)
        {
            File.AppendAllText(log, line + "\n");
        }
    }

    [Theory]
    [MemberData(nameof(RgbPairs))]
    public void BuiltInRgbMatchesLcms(string src, string dst, int intent, bool bpc)
    {
        var i = (OneImage.RenderingIntent)intent;
        var r = Compare(Named(src), Named(dst), i, bpc);
        Report($"{src}->{dst} {i} bpc={bpc}", r);
        Assert.True(r.Max <= 1.0 || r.MaxLsb <= 2, $"max dE00 {r.Max}, max LSB {r.MaxLsb}");
    }
}
