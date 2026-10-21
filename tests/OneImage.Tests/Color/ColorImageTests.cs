using OneImage.Kernels;

namespace OneImage.Tests;

public class ColorImageTests
{
    private static Image Gradient(int w, int h, int lo = 24, int hi = 231)
    {
        var px = new Rgb24[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = new Rgb24((byte)(lo + (x * (hi - lo) / (w - 1))), (byte)(lo + (y * (hi - lo) / (h - 1))), (byte)(lo + ((x + y) * (hi - lo) / (w + h - 2))));
            }
        }

        return Image.FromPixels<Rgb24>(px, w, h);
    }

    [Fact]
    public void ConvertColorEmbedsTargetProfileAndDescribesNode()
    {
        Image img = Gradient(300, 200).ConvertColor(ColorProfile.DisplayP3);
        Assert.True(img.Metadata.IccProfile.Span.SequenceEqual(ColorProfile.DisplayP3.IccBytes.Span));
        Assert.Same(ColorProfile.DisplayP3.Description, ColorProfile.DisplayP3.Description);
        string text = img.DescribeGraph();
        Assert.Contains("convert_color(profile=", text, StringComparison.Ordinal);
        Assert.Contains("intent=RelativeColorimetric", text, StringComparison.Ordinal);
        Assert.Equal(PixelFormat.Rgb8, img.Format);
        Assert.Equal(ColorModel.Rgb, img.ColorProfile.Model);

        // Round trip through P3 stays within rounding of the source.
        Rgb24[] a = Gradient(300, 200).ToArray<Rgb24>();
        Rgb24[] b = img.ConvertColor(ColorProfile.Srgb).ToArray<Rgb24>();
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(a[i].R - b[i].R), Math.Max(Math.Abs(a[i].G - b[i].G), Math.Abs(a[i].B - b[i].B))));
        }

        Assert.True(worst <= 4, $"worst {worst}"); // the inverse matrix amplifies 8-bit rounding in P3 by up to about 3x
    }

    [Fact]
    public void CmykImageConvertsThroughDefaultProfileAndEmbeddedProfileIsHonored()
    {
        var cmyk = new Cmyk32[4];
        cmyk[0] = new Cmyk32(0, 0, 0, 255); // K only in pixel 0, paper white elsewhere
        using var canvas = new Canvas(4, 1, PixelFormat.Cmyk8);
        canvas.CopyFrom<Cmyk32>(cmyk, new ImageRect(0, 0, 4, 1));
        Image img = canvas.Snapshot();
        Rgb24[] rgb = img.ConvertTo(PixelFormat.Rgb8).ToArray<Rgb24>();
        Assert.Equal(new Rgb24(255, 255, 255), rgb[1]);
        Assert.True(rgb[0].R < 60 && rgb[0].G < 60 && rgb[0].B < 60);
        Assert.Equal(ColorProfile.GenericCmyk, img.ColorProfile);

        // Round trip RGB -> CMYK -> RGB keeps in-gamut colors.
        Image sw = Gradient(64, 64, 100, 190);
        Rgb24[] src = sw.ToArray<Rgb24>();
        Rgb24[] back = sw.ConvertTo(PixelFormat.Cmyk8).ConvertTo(PixelFormat.Rgb8).ToArray<Rgb24>();
        double mean = 0;
        for (int i = 0; i < src.Length; i++)
        {
            mean += (Math.Abs(src[i].R - back[i].R) + Math.Abs(src[i].G - back[i].G) + Math.Abs(src[i].B - back[i].B)) / 3.0;
        }

        Assert.True(mean / src.Length < 3, $"mean abs error {mean / src.Length}");
    }

    [Fact]
    public void AlphaIsPreservedAndPremultipliedRoundTrips()
    {
        var px = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            px[i] = new Rgba32((byte)i, (byte)(255 - i), (byte)(i / 2), (byte)i);
        }

        Image img = Image.FromPixels<Rgba32>(px, 16, 16);
        Rgba32[] straight = img.ConvertColor(ColorProfile.DisplayP3).ToArray<Rgba32>();
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(px[i].A, straight[i].A);
        }

        Image pre = img.ConvertTo(PixelFormat.Rgba8Premultiplied).ConvertColor(ColorProfile.DisplayP3);
        Assert.Equal(AlphaMode.Premultiplied, pre.Format.Alpha);
        Rgba32[] viaPre = pre.ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
        for (int i = 32; i < 256; i++)
        {
            Assert.True(Math.Abs(viaPre[i].R - straight[i].R) <= 6, $"{i}: {viaPre[i].R} vs {straight[i].R}");
        }
    }

    [Fact]
    public void LabFloatRoundTripsThroughSrgb()
    {
        Image img = Gradient(32, 32);
        Image lab = img.ConvertTo(PixelFormat.LabF32);
        Assert.Equal(ColorModel.Lab, lab.Format.Model);
        Image back = lab.ConvertTo(PixelFormat.Rgb8);
        Rgb24[] a = img.ToArray<Rgb24>();
        Rgb24[] b = back.ToArray<Rgb24>();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.True(Math.Abs(a[i].R - b[i].R) <= 1 && Math.Abs(a[i].G - b[i].G) <= 1 && Math.Abs(a[i].B - b[i].B) <= 1, $"{i}");
        }
    }

    [Fact]
    public void LinearLightHookRoundTrips()
    {
        Image img = Gradient(50, 40);
        Image lin = img.ToLinearLight(out ColorProfile original);
        Assert.Equal(SampleType.F32, lin.Format.Sample);
        Assert.True(lin.ColorProfile.IsLinear);
        Image back = lin.FromLinearLight(original, PixelFormat.Rgb8);
        Rgb24[] a = img.ToArray<Rgb24>();
        Rgb24[] b = back.ToArray<Rgb24>();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.True(Math.Abs(a[i].R - b[i].R) <= 1 && Math.Abs(a[i].G - b[i].G) <= 1 && Math.Abs(a[i].B - b[i].B) <= 1, $"{i}");
        }
    }

    [Fact]
    public void SrgbTransferMatchesExactFormula()
    {
        var v = new float[10001];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = i / 10000f;
        }

        float[] enc = (float[])v.Clone();
        SrgbTransfer.Encode(enc);
        for (int i = 0; i < v.Length; i++)
        {
            Assert.True(Math.Abs((enc[i] * 255) - (SrgbTransfer.EncodeOne(v[i]) * 255)) < 0.01, $"{v[i]}");
        }

        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        float[] dec = new float[256];
        SrgbTransfer.Decode8(all, dec);
        float[] dec2 = all.Select(b => b / 255f).ToArray();
        SrgbTransfer.Decode(dec2);
        Assert.Equal(dec2, dec);
    }

    [Fact]
    public void LinearVariantsAndProfileProperties()
    {
        Assert.True(ColorProfile.LinearSrgb.IsLinear);
        Assert.False(ColorProfile.Srgb.IsLinear);
        Assert.True(ColorProfile.Srgb.LinearVariant!.IsLinear);
        Assert.Null(ColorProfile.GenericCmyk.LinearVariant);
        Assert.Equal(ColorModel.Gray, ColorProfile.Gray.Model);
        Assert.True(ColorProfile.Srgb.IccBytes.Length < 600, $"compact sRGB is {ColorProfile.Srgb.IccBytes.Length} bytes");
        Assert.Equal(ColorModel.Lab, ColorProfile.LabD50.Model);
        foreach (ColorProfile p in new[] { ColorProfile.DisplayP3, ColorProfile.WideGamutRgb, ColorProfile.ProPhoto, ColorProfile.Rec2020, ColorProfile.LinearDisplayP3, ColorProfile.LinearWideGamutRgb, ColorProfile.LinearProPhoto, ColorProfile.LinearRec2020, ColorProfile.LinearGray })
        {
            Assert.StartsWith(Library.Name, p.Description);
            Assert.DoesNotContain("Adobe", p.Description);
            _ = new ImageMagick.ColorProfile(p.IccBytes.ToArray());
        }

        // A parsed copy of a built-in profile derives the same linear variant.
        ColorProfile parsed = ColorProfile.FromIcc(ColorProfile.DisplayP3.IccBytes.Span);
        Assert.True(parsed.LinearVariant!.IsLinear);
    }

    [Fact]
    public void GeneratedProfilesAreByteIdenticalAcrossBuilds()
    {
        // Built-ins are generated once per process; compare against a fresh generation through the writer path.
        byte[] a = ColorProfile.Srgb.IccBytes.ToArray();
        byte[] b = ColorProfile.FromIcc(a).IccBytes.ToArray();
        Assert.True(a.AsSpan().SequenceEqual(b));
        Assert.Equal("dd7dad33", Fnv(ColorProfile.DisplayP3.IccBytes.Span).ToString("x8"), ignoreCase: true);
    }

    private static uint Fnv(ReadOnlySpan<byte> d)
    {
        uint h = 2166136261;
        foreach (byte b in d)
        {
            h = (h ^ b) * 16777619;
        }

        return h;
    }

    [Fact]
    public void ReportHardwarePaths() =>
        ColorAccuracyTests.Report($"paths: Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated} Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Avx512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}", (0, 0, 0));

    [Fact]
    public void GridKernelIsBitIdenticalOnEveryHardwarePath()
    {
        // The kernel uses only IEEE add, subtract and multiply in a fixed order, so the same input gives the same bits with
        // AVX-512, AVX2 or no intrinsics. A golden hash pins that: this test is run under each DOTNET_Enable* setting.
        var rnd = new Random(77);
        foreach ((int ins, int outs, int pts) in new[] { (3, 3, 9), (3, 4, 5), (4, 3, 5), (1, 3, 33) })
        {
            var g = new Grid(ins, pts, outs);
            for (int i = 0; i < g.Entries; i++)
            {
                for (int c = 0; c < outs; c++)
                {
                    g.Data[(i * 4) + c] = (float)rnd.NextDouble();
                }
            }

            foreach (int n in new[] { 1, 7, 8, 15, 16, 17, 31, 33 })
            {
                float[] src = new float[n * ins];
                for (int i = 0; i < src.Length; i++)
                {
                    src[i] = (float)((rnd.NextDouble() * 1.2) - 0.1);
                }

                float[] dst = new float[n * outs];
                GridKernel.Eval(g, src, dst, n);

                // Reference: plain scalar multilinear-free tetrahedral evaluation in double must agree closely.
                for (int p = 0; p < n; p++)
                {
                    double[] r = ReferenceEval(g, src.AsSpan(p * ins, ins));
                    for (int c = 0; c < outs; c++)
                    {
                        Assert.True(Math.Abs(r[c] - dst[(p * outs) + c]) < 1e-5, $"ins {ins} n {n} p {p}");
                    }
                }
            }
        }

        // Golden bits.
        var gg = new Grid(3, 17, 3);
        var rr = new Random(5);
        for (int i = 0; i < gg.Entries; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                gg.Data[(i * 4) + c] = (float)rr.NextDouble();
            }
        }

        float[] s2 = new float[3 * 1000];
        for (int i = 0; i < s2.Length; i++)
        {
            s2[i] = (float)rr.NextDouble();
        }

        float[] d2 = new float[3 * 1000];
        GridKernel.Eval(gg, s2, d2, 1000);
        uint h = 2166136261;
        foreach (float f in d2)
        {
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(f)) * 16777619;
        }

        Assert.Equal(GoldenGridHash, h);
    }

    private const uint GoldenGridHash = 1715636419;

    private static double[] ReferenceEval(Grid g, ReadOnlySpan<float> x)
    {
        // Independent formulation: tetrahedral weights by sorting the fractional parts, 4D as linear blend of two 3D lookups.
        int ins = g.Inputs;
        int first = ins == 4 ? 1 : 0;
        int n3 = ins == 1 ? 0 : 3;
        int[] dims = g.Dims;
        double[] Tetra(int baseEntry, ReadOnlySpan<float> v, int axis0)
        {
            int[] idx = new int[3];
            double[] f = new double[3];
            for (int a = 0; a < 3; a++)
            {
                int d = dims[axis0 + a];
                double pos = Math.Clamp((double)v[a], 0, 1) * (d - 1);
                idx[a] = Math.Min((int)pos, d - 2);
                f[a] = pos - idx[a];
            }

            int[] order = [0, 1, 2];
            Array.Sort(order, (p, q) => f[q].CompareTo(f[p]));
            int sx = dims[axis0 + 1] * dims[axis0 + 2], sy = dims[axis0 + 2];
            int[] stride = [sx, sy, 1];
            int e = baseEntry + (idx[0] * sx) + (idx[1] * sy) + idx[2];
            double[] res = new double[g.Outputs];
            double[] prev = new double[g.Outputs];
            for (int c = 0; c < g.Outputs; c++)
            {
                prev[c] = g.Data[(e * 4) + c];
                res[c] = prev[c];
            }

            double prevF = 1;
            int cur = e;
            foreach (int a in order)
            {
                cur += stride[a];
                for (int c = 0; c < g.Outputs; c++)
                {
                    double val = g.Data[(cur * 4) + c];
                    res[c] += f[a] * (val - prev[c]);
                    prev[c] = val;
                }

                prevF = f[a];
            }

            _ = prevF;
            return res;
        }

        if (ins == 1)
        {
            double pos = Math.Clamp((double)x[0], 0, 1) * (dims[0] - 1);
            int i0 = Math.Min((int)pos, dims[0] - 2);
            double f = pos - i0;
            double[] r1 = new double[g.Outputs];
            for (int c = 0; c < g.Outputs; c++)
            {
                r1[c] = g.Data[(i0 * 4) + c] + (f * (g.Data[((i0 + 1) * 4) + c] - g.Data[(i0 * 4) + c]));
            }

            return r1;
        }

        if (ins == 3)
        {
            return Tetra(0, x, 0);
        }

        double p0 = Math.Clamp((double)x[0], 0, 1) * (dims[0] - 1);
        int j = Math.Min((int)p0, dims[0] - 2);
        double fj = p0 - j;
        int stride3 = dims[1] * dims[2] * dims[3];
        double[] lo = Tetra(j * stride3, x[1..], first);
        double[] hi = Tetra((j + 1) * stride3, x[1..], first);
        _ = n3;
        return lo.Zip(hi, (a, b) => a + (fj * (b - a))).ToArray();
    }
}
