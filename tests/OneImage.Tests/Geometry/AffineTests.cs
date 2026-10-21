using Xunit;

namespace OneImage.Tests.Geometry;

internal static class ReferenceAffine
{
    private static double Decode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double Encode(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    private static int Edge(int i, int n, EdgeMode mode)
    {
        if (i >= 0 && i < n)
        {
            return i;
        }

        switch (mode)
        {
            case EdgeMode.Clamp:
                return Math.Clamp(i, 0, n - 1);
            case EdgeMode.Wrap:
                return (((i % n) + n) % n);
            case EdgeMode.Mirror:
                while (i < 0 || i >= n)
                {
                    i = i < 0 ? -i - 1 : (2 * n) - 1 - i;
                }

                return i;
            default:
                return -1;
        }
    }

    /// <summary>Straightforward double-precision version of the documented definition. Returns normalized straight RGBA.</summary>
    public static double[] Run(Rgba32[] src, int w, int h, double a, double b, double c, double d, double tx, double ty, double ox, double oy, int ow, int oh, ResamplingFilter f, EdgeMode edge, Rgba32 bgColor, bool linear)
    {
        double det = (a * d) - (b * c);
        double ia = d / det, ib = -b / det, ic = -c / det, id = a / det;
        int support = f switch { ResamplingFilter.Nearest or ResamplingFilter.Box => 0, ResamplingFilter.Bilinear => 1, ResamplingFilter.Lanczos3 => 3, _ => 2 };

        // premultiplied (optionally linear) source
        double[] pm = new double[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            double al = src[i].A / 255.0;
            pm[i * 4] = (linear ? Decode(src[i].R / 255.0) : src[i].R / 255.0) * al;
            pm[(i * 4) + 1] = (linear ? Decode(src[i].G / 255.0) : src[i].G / 255.0) * al;
            pm[(i * 4) + 2] = (linear ? Decode(src[i].B / 255.0) : src[i].B / 255.0) * al;
            pm[(i * 4) + 3] = al;
        }

        double bal = bgColor.A / 255.0;
        double[] bg = [(linear ? Decode(bgColor.R / 255.0) : bgColor.R / 255.0) * bal, (linear ? Decode(bgColor.G / 255.0) : bgColor.G / 255.0) * bal, (linear ? Decode(bgColor.B / 255.0) : bgColor.B / 255.0) * bal, bal];
        double[] res = new double[ow * oh * 4];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                double X = ox + x + 0.5 - tx;
                double Y = oy + y + 0.5 - ty;
                double px = (ia * X) + (ib * Y) - 0.5;
                double py = (ic * X) + (id * Y) - 0.5;
                double[] acc = new double[4];
                if (support == 0)
                {
                    int ix = Edge((int)Math.Floor(px + 0.5), w, edge);
                    int iy = Edge((int)Math.Floor(py + 0.5), h, edge);
                    for (int k = 0; k < 4; k++)
                    {
                        acc[k] = ix < 0 || iy < 0 ? bg[k] : pm[(((iy * w) + ix) * 4) + k];
                    }
                }
                else
                {
                    int bx = (int)Math.Floor(px);
                    int by = (int)Math.Floor(py);
                    double sx = 0, sy = 0;
                    double[] wx = new double[2 * support], wy = new double[2 * support];
                    for (int t = 0; t < 2 * support; t++)
                    {
                        wx[t] = FilterFunctions.Evaluate(f, bx - support + 1 + t - px);
                        wy[t] = FilterFunctions.Evaluate(f, by - support + 1 + t - py);
                        sx += wx[t];
                        sy += wy[t];
                    }

                    for (int j = 0; j < 2 * support; j++)
                    {
                        int iy = Edge(by - support + 1 + j, h, edge);
                        for (int i = 0; i < 2 * support; i++)
                        {
                            int ix = Edge(bx - support + 1 + i, w, edge);
                            for (int k = 0; k < 4; k++)
                            {
                                acc[k] += (wx[i] / sx) * (wy[j] / sy) * (ix < 0 || iy < 0 ? bg[k] : pm[(((iy * w) + ix) * 4) + k]);
                            }
                        }
                    }
                }

                double al = acc[3];
                int o = ((y * ow) + x) * 4;
                for (int k = 0; k < 3; k++)
                {
                    double v = al > 0 ? Math.Clamp(acc[k] / al, 0, 1) : 0;
                    res[o + k] = linear ? Encode(v) : v;
                }

                res[o + 3] = Math.Clamp(al, 0, 1);
            }
        }

        return res;
    }

    public static (double Ox, double Oy, int W, int H) Bounds(int w, int h, double a, double b, double c, double d, double tx, double ty)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach ((double x, double y) in new[] { (0.0, 0.0), (w, 0.0), (0.0, h), ((double)w, h) })
        {
            xs.Add((a * x) + (b * y) + tx);
            ys.Add((c * x) + (d * y) + ty);
        }

        return (xs.Min(), ys.Min(), (int)Math.Ceiling(xs.Max() - xs.Min() - 1e-6), (int)Math.Ceiling(ys.Max() - ys.Min() - 1e-6));
    }
}

public class AffineTests
{
    private static Rgba32[] Source(int w, int h, bool translucent)
    {
        var rnd = new Random(77);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), translucent && rnd.Next(4) == 0 ? (byte)rnd.Next(256) : (byte)255);
        }

        return px;
    }

    private static double Worst(Rgba32[] got, double[] want)
    {
        double worst = 0;
        for (int i = 0; i < got.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(got[i].R - (want[i * 4] * 255)));
            worst = Math.Max(worst, Math.Abs(got[i].G - (want[(i * 4) + 1] * 255)));
            worst = Math.Max(worst, Math.Abs(got[i].B - (want[(i * 4) + 2] * 255)));
            worst = Math.Max(worst, Math.Abs(got[i].A - (want[(i * 4) + 3] * 255)));
        }

        return worst;
    }

    public static IEnumerable<TheoryDataRow<ResamplingFilter, EdgeMode, bool>> RotateCases()
    {
        foreach (ResamplingFilter f in new[] { ResamplingFilter.Nearest, ResamplingFilter.Bilinear, ResamplingFilter.CatmullRom, ResamplingFilter.Mitchell, ResamplingFilter.Lanczos2, ResamplingFilter.Lanczos3 })
        {
            foreach (EdgeMode e in Enum.GetValues<EdgeMode>())
            {
                yield return new(f, e, true);
            }

            yield return new(f, EdgeMode.Constant, false);
        }
    }

    [Theory]
    [MemberData(nameof(RotateCases))]
    public void Rotate17MatchesTheDoubleReference(ResamplingFilter f, EdgeMode edge, bool linear)
    {
        const int W = 83;
        const int H = 57;
        Rgba32[] src = Source(W, H, translucent: true);
        Rgba32 bg = new(30, 160, 90, 120);
        double rad = 17 * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = W / 2.0, cy = H / 2.0;
        double tx = cx - ((cos * cx) - (sin * cy));
        double ty = cy - ((sin * cx) + (cos * cy));
        (double ox, double oy, int ow, int oh) = ReferenceAffine.Bounds(W, H, cos, -sin, sin, cos, tx, ty);

        Image r = Image.FromPixels<Rgba32>(src, W, H).Rotate(17, true, bg, f, edge, linear);
        Assert.Equal((ow, oh), (r.Width, r.Height));
        double[] want = ReferenceAffine.Run(src, W, H, cos, -sin, sin, cos, tx, ty, ox, oy, ow, oh, f == ResamplingFilter.Auto ? ResamplingFilter.CatmullRom : f, edge, bg, linear);
        double worst = Worst(r.ToArray<Rgba32>(), want);
        TestContext.Current.SendDiagnosticMessage($"affine {f} {edge} linear={linear}: max error {worst:F3} LSB");
        Assert.True(worst <= 1.0, $"{f}/{edge}/linear={linear}: max error {worst:F3} LSB");
    }

    [Fact]
    public void ExpandFalseKeepsTheSizeAndGeneralAffineMatchesTheReference()
    {
        const int W = 70;
        const int H = 50;
        Rgba32[] src = Source(W, H, translucent: false);
        // shear, scale and translation
        double a = 1.2, b = 0.35, c = -0.1, d = 0.8, tx = 7.5, ty = -3.25;
        Rgba32 bg = new(255, 255, 255, 255);
        foreach (bool expand in new[] { true, false })
        {
            Image r = Image.FromPixels<Rgba32>(src, W, H).Affine(a, b, c, d, tx, ty, expand, bg, ResamplingFilter.Lanczos3);
            (double ox, double oy, int ow, int oh) = expand ? ReferenceAffine.Bounds(W, H, a, b, c, d, tx, ty) : (0, 0, W, H);
            Assert.Equal((ow, oh), (r.Width, r.Height));
            double[] want = ReferenceAffine.Run(src, W, H, a, b, c, d, tx, ty, ox, oy, ow, oh, ResamplingFilter.Lanczos3, EdgeMode.Constant, bg, true);
            Assert.True(Worst(r.ToArray<Rgba32>(), want) <= 1.0);
        }
    }

    [Theory]
    [InlineData(100, 100, 45, 142, 142)]
    [InlineData(200, 100, 30, 224, 187)]
    [InlineData(300, 100, 90.0000001, 100, 300)]
    public void ExpandedRotationBoundsAreTheCeilingOfTheExtent(int w, int h, double angle, int ew, int eh)
    {
        Image r = Image.FromNode(new Resample.RawBytesNode(new byte[w * h * 4], w, h, PixelFormat.Rgba8)).Rotate(angle);
        Assert.Equal((ew, eh), (r.Width, r.Height));
        Image keep = Image.FromNode(new Resample.RawBytesNode(new byte[w * h * 4], w, h, PixelFormat.Rgba8)).Rotate(angle, expand: false);
        Assert.Equal((w, h), (keep.Width, keep.Height));
    }

    [Fact]
    public void ConstantImagesStayConstantAndIdentityAffineIsExact()
    {
        Rgba32 c = new(17, 130, 243, 255);
        Image solid = Image.FromPixels<Rgba32>(Enumerable.Repeat(c, 90 * 70).ToArray(), 90, 70);
        foreach (ResamplingFilter f in new[] { ResamplingFilter.Bilinear, ResamplingFilter.CatmullRom, ResamplingFilter.Lanczos3 })
        {
            // Without a background edge every output pixel is the constant.
            Assert.All(solid.Rotate(33, filter: f, edge: EdgeMode.Clamp).ToArray<Rgba32>(), p => Assert.Equal(c, p));
            Assert.All(solid.Rotate(33, filter: f, edge: EdgeMode.Mirror).ToArray<Rgba32>(), p => Assert.Equal(c, p));
        }

        Image img = OrientTests.Make(130, 90);
        Assert.Equal(img.ToArray<Rgba32>(), img.Affine(1, 0, 0, 1).ToArray<Rgba32>());
        Assert.Equal(img.ToArray<Rgba32>(), img.Affine(1, 0, 0, 1, 40, 11).ToArray<Rgba32>());
    }

    [Fact]
    public void NearestWithIntegerTranslationMovesPixelsExactly()
    {
        Image img = OrientTests.Make(100, 80);
        Rgba32[] got = img.Affine(1, 0, 0, 1, 5, 3, expand: false, filter: ResamplingFilter.Nearest).ToArray<Rgba32>();
        Assert.Equal(default, got[0]);
        Assert.Equal(OrientTests.Px(0, 0), got[(3 * 100) + 5]);
        Assert.Equal(OrientTests.Px(94, 76), got[(79 * 100) + 99]);
    }

    [Fact]
    public void ResultDoesNotDependOnThreadsOrTileOrder()
    {
        Image r = OrientTests.Make(400, 300).Rotate(23.5, filter: ResamplingFilter.Lanczos3);
        Rgba32[] a = r.ToArray<Rgba32>(maxDegree: 1);
        Rgba32[] b = r.ToArray<Rgba32>(maxDegree: 8);
        Assert.Equal(a, b);
        Image fresh = OrientTests.Make(400, 300).Rotate(23.5, filter: ResamplingFilter.Lanczos3);
        var piece = new Rgba32[128 * 128];
        for (int ty = r.Height / 128; ty >= 0; ty--)
        {
            for (int tx = r.Width / 128; tx >= 0; tx--)
            {
                var rect = new ImageRect(tx * 128, ty * 128, Math.Min(128, r.Width - (tx * 128)), Math.Min(128, r.Height - (ty * 128)));
                if (rect.IsEmpty)
                {
                    continue;
                }

                fresh.CopyPixelsTo<Rgba32>(piece, rect, maxDegree: 1);
                for (int y = 0; y < rect.Height; y++)
                {
                    for (int x = 0; x < rect.Width; x++)
                    {
                        Assert.Equal(a[((rect.Y + y) * r.Width) + rect.X + x], piece[(y * rect.Width) + x]);
                    }
                }
            }
        }
    }

    [Fact]
    public void GrayAndRgbFormatsRotate()
    {
        Gray8[] px = Enumerable.Range(0, 60 * 40).Select(i => new Gray8((byte)(i % 251))).ToArray();
        Image g = Image.FromPixels<Gray8>(px, 60, 40).Rotate(12, background: new Rgba32(255, 255, 255, 255));
        Assert.Equal(PixelFormat.Gray8, g.Format);
        Gray8[] got = g.ToArray<Gray8>();
        Assert.Equal(255, got[0].V);
        Rgb24[] rgb = Enumerable.Repeat(new Rgb24(10, 20, 30), 50 * 50).ToArray();
        Assert.Equal(new Rgb24(10, 20, 30), Image.FromPixels<Rgb24>(rgb, 50, 50).Rotate(20, edge: EdgeMode.Clamp).ToArray<Rgb24>()[100]);
    }

    [Fact]
    public void SingularAndNonFiniteMatricesAreRejected()
    {
        Image img = OrientTests.Make(10, 10);
        Assert.Throws<ImageOperationException>(() => img.Affine(1, 2, 2, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Affine(double.NaN, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Rotate(10, filter: (ResamplingFilter)50));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Rotate(10, edge: (EdgeMode)9));
    }

    [Fact]
    public void WeightTableLookupTracksTheExactKernels()
    {
        foreach (ResamplingFilter f in new[] { ResamplingFilter.Bilinear, ResamplingFilter.CatmullRom, ResamplingFilter.Mitchell, ResamplingFilter.Lanczos2, ResamplingFilter.Lanczos3 })
        {
            float[] table = Kernels.AffineFilterTable.Get(f);
            double worst = 0;
            for (double x = -3.2; x <= 3.2; x += 0.00137)
            {
                worst = Math.Max(worst, Math.Abs(Kernels.AffineFilterTable.Weight(table, (float)x) - FilterFunctions.Evaluate(f, x)));
            }

            Assert.True(worst < 1e-5, $"{f}: {worst}");
        }
    }
}
