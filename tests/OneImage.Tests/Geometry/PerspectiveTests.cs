using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Is = SixLabors.ImageSharp;
using IsPx = SixLabors.ImageSharp.PixelFormats;

namespace OneImage.Tests.Geometry;

internal static class ReferencePerspective
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
                return ((i % n) + n) % n;
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

    public static (double Ox, double Oy, int W, int H) Bounds(int w, int h, ProjectiveMatrix m)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach ((double x, double y) in new[] { (0.0, 0.0), (w, 0.0), (0.0, h), ((double)w, h) })
        {
            (double X, double Y) = m.Transform(x, y);
            xs.Add(X);
            ys.Add(Y);
        }

        return (xs.Min(), ys.Min(), (int)Math.Ceiling(xs.Max() - xs.Min() - 1e-6), (int)Math.Ceiling(ys.Max() - ys.Min() - 1e-6));
    }

    /// <summary>Direct double-precision version of the documented definition, using the true inverse. Returns straight RGBA in [0, 1].</summary>
    public static double[] Run(Rgba32[] src, int w, int h, ProjectiveMatrix m, double ox, double oy, int ow, int oh, ResamplingFilter f, EdgeMode edge, Rgba32 bgColor, bool linear)
    {
        double det = m.Determinant;
        ProjectiveMatrix adj = ProjectiveMatrix.Adjugate(m);
        int support = f is ResamplingFilter.Nearest or ResamplingFilter.Box ? 0 : (int)Math.Ceiling(FilterFunctions.Support(f));

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
        double[] bg =
        [
            (linear ? Decode(bgColor.R / 255.0) : bgColor.R / 255.0) * bal,
            (linear ? Decode(bgColor.G / 255.0) : bgColor.G / 255.0) * bal,
            (linear ? Decode(bgColor.B / 255.0) : bgColor.B / 255.0) * bal,
            bal,
        ];

        double[] res = new double[ow * oh * 4];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                double X = ox + x + 0.5;
                double Y = oy + y + 0.5;
                double u = (adj.M11 * X) + (adj.M12 * Y) + adj.M13;
                double v = (adj.M21 * X) + (adj.M22 * Y) + adj.M23;
                double s = (adj.M31 * X) + (adj.M32 * Y) + adj.M33;
                double[] acc = new double[4];
                if (s / det <= 0)
                {
                    Array.Copy(bg, acc, 4);
                }
                else
                {
                    double px = (u / s) - 0.5;
                    double py = (v / s) - 0.5;
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
                }

                double alpha = acc[3];
                int o = ((y * ow) + x) * 4;
                for (int k = 0; k < 3; k++)
                {
                    double c = alpha > 0 ? Math.Clamp(acc[k] / alpha, 0, 1) : 0;
                    res[o + k] = linear ? Encode(c) : c;
                }

                res[o + 3] = Math.Clamp(alpha, 0, 1);
            }
        }

        return res;
    }
}

public class PerspectiveTests
{
    private static Rgba32[] Source(int w, int h, bool translucent)
    {
        var rnd = new Random(91);
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

    private static readonly (double X, double Y)[] s_quad = [(12, 5), (95, -8), (81, 70), (-6, 55)];

    public static IEnumerable<TheoryDataRow<string, ResamplingFilter, EdgeMode, bool>> ReferenceCases()
    {
        foreach (string shape in new[] { "taper", "quad", "perspective-rotate" })
        {
            foreach (ResamplingFilter f in new[] { ResamplingFilter.Nearest, ResamplingFilter.Bilinear, ResamplingFilter.CatmullRom, ResamplingFilter.Lanczos3, ResamplingFilter.Hermite, ResamplingFilter.Robidoux, ResamplingFilter.Welch, ResamplingFilter.Lanczos5, ResamplingFilter.Lanczos8 })
            {
                yield return new(shape, f, EdgeMode.Constant, true);
            }

            foreach (EdgeMode e in new[] { EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Wrap })
            {
                yield return new(shape, ResamplingFilter.CatmullRom, e, shape == "quad");
            }
        }
    }

    private static ProjectiveMatrix Shape(string shape, int w, int h) => shape switch
    {
        "taper" => new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0.55).BuildMatrix(w, h),
        "quad" => ProjectiveMatrix.FromRectangle(w, h, s_quad),
        _ => new ProjectiveTransformBuilder().AppendRotationDegrees(21).AppendMatrix(new ProjectiveMatrix(1, 0.1, 0, -0.05, 1, 0, 0.002, 0.0015, 1)).BuildMatrix(w, h),
    };

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void MatchesTheDoubleReference(string shape, ResamplingFilter f, EdgeMode edge, bool linear)
    {
        const int W = 81;
        const int H = 60;
        Rgba32[] src = Source(W, H, translucent: true);
        Rgba32 bg = new(30, 160, 90, 120);
        ProjectiveMatrix m = Shape(shape, W, H);
        (double ox, double oy, int ow, int oh) = ReferencePerspective.Bounds(W, H, m);

        Image r = Image.FromPixels<Rgba32>(src, W, H).Perspective(m, true, bg, f, edge, linear);
        Assert.Equal((ow, oh), (r.Width, r.Height));
        double[] want = ReferencePerspective.Run(src, W, H, m, ox, oy, ow, oh, f, edge, bg, linear);
        if (f == ResamplingFilter.Nearest)
        {
            // Nearest flips to the neighbor when a position lands exactly on a pixel edge, where rounding in the last bit decides; allow a handful.
            Rgba32[] px = r.ToArray<Rgba32>();
            int bad = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (Math.Abs(px[i].R - (want[i * 4] * 255)) > 1 || Math.Abs(px[i].A - (want[(i * 4) + 3] * 255)) > 1)
                {
                    bad++;
                }
            }

            Assert.True(bad <= px.Length / 500, $"{shape}/{edge}: {bad} of {px.Length} pixels differ");
            return;
        }

        double worst = Worst(r.ToArray<Rgba32>(), want);
        TestContext.Current.SendDiagnosticMessage($"perspective {shape} {f} {edge} linear={linear}: max error {worst:F3} LSB");
        Assert.True(worst <= 1.0, $"{shape}/{f}/{edge}/linear={linear}: max error {worst:F3} LSB");
    }

    [Fact]
    public void ExpandFalseKeepsTheSizeAndFarSidePixelsAreBackground()
    {
        const int W = 100;
        const int H = 60;
        Rgba32[] src = Source(W, H, translucent: false);
        Rgba32 bg = new(255, 255, 255, 255);
        // Mild perspective, applied as given in the input frame.
        var mild = new ProjectiveMatrix(1.1, 0.05, 4, -0.04, 0.95, 2, 0.0008, -0.0004, 1);
        // The horizon crosses the output frame: dest x below 50 lies beyond it, and the source side only reaches dest x > 100.
        var far = new ProjectiveMatrix(1, 0, 0, 0, 1, 0, -0.005, 0, 1).Then(ProjectiveMatrix.Translation(250, 0));
        foreach (ProjectiveMatrix m in new[] { mild, far })
        {
            Image r = Image.FromPixels<Rgba32>(src, W, H).Perspective(m, expand: false, background: bg, filter: ResamplingFilter.Lanczos3);
            Assert.Equal((W, H), (r.Width, r.Height));
            double[] want = ReferencePerspective.Run(src, W, H, m, 0, 0, W, H, ResamplingFilter.Lanczos3, EdgeMode.Constant, bg, true);
            Assert.True(Worst(r.ToArray<Rgba32>(), want) <= 1.0);
        }
    }

    [Fact]
    public void IdentityIsExactAndSizeIsKept()
    {
        Image img = OrientTests.Make(130, 90);
        Rgba32[] want = img.ToArray<Rgba32>();
        Assert.Equal(want, img.Perspective(ProjectiveMatrix.Identity).ToArray<Rgba32>());
        Assert.Equal(want, img.Perspective(new ProjectiveTransformBuilder()).ToArray<Rgba32>());
        // A multiple of the identity is the identity.
        Assert.Equal(want, img.Perspective(new ProjectiveMatrix(-3, 0, 0, 0, -3, 0, 0, 0, -3)).ToArray<Rgba32>());
        // A whole-pixel translation with expand: the output is re-anchored, so the picture is unchanged.
        Assert.Equal(want, img.Perspective(ProjectiveMatrix.Translation(40, 11)).ToArray<Rgba32>());
    }

    [Fact]
    public void QuadCornersLandWhereSpecified()
    {
        (double X, double Y)[] quad = [(30, 10), (150, 40), (120, 130), (10, 100)];
        var m = ProjectiveMatrix.FromRectangle(80, 60, quad);
        ReadOnlySpan<(double X, double Y)> rect = [(0, 0), (80, 0), (80, 60), (0, 60)];
        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = m.Transform(rect[i].X, rect[i].Y);
            Assert.Equal(quad[i].X, x, 9);
            Assert.Equal(quad[i].Y, y, 9);
        }

        // General quad-to-quad.
        (double X, double Y)[] other = [(5, 5), (70, 12), (66, 50), (-3, 41)];
        var q = ProjectiveMatrix.FromQuads(quad, other);
        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = q.Transform(quad[i].X, quad[i].Y);
            Assert.Equal(other[i].X, x, 9);
            Assert.Equal(other[i].Y, y, 9);
        }

        // In the picture: four colored corner blocks end up just inside the matching destination corners.
        var src = new Rgba32[80 * 60];
        Rgba32[] colors = [new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255), new(255, 255, 0, 255)];
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                int quadrant = (y < 30 ? 0 : 2) + (x < 40 ? 0 : 1);
                // quadrants: 0 TL, 1 TR, 2 BL, 3 BR
                src[(y * 80) + x] = quadrant switch { 0 => colors[0], 1 => colors[1], 3 => colors[2], _ => colors[3] };
            }
        }

        Image img = Image.FromPixels<Rgba32>(src, 80, 60).Perspective(quad, filter: ResamplingFilter.Nearest, linear: false);
        double minX = quad.Min(p => p.X), minY = quad.Min(p => p.Y);
        Assert.Equal((int)Math.Ceiling(150 - minX - 1e-6), img.Width);
        Assert.Equal((int)Math.Ceiling(130 - minY - 1e-6), img.Height);
        Rgba32[] outPx = img.ToArray<Rgba32>();
        (double X, double Y)[] inner = [(0.5, 0.5), (79.5, 0.5), (79.5, 59.5), (0.5, 59.5)];
        Rgba32[] expect = [colors[0], colors[1], colors[2], colors[3]];
        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = m.Transform(inner[i].X, inner[i].Y);
            int px = (int)Math.Floor(x - minX);
            int py = (int)Math.Floor(y - minY);
            Assert.Equal(expect[i], outPx[(py * img.Width) + px]);
        }
    }

    [Fact]
    public void BuilderStepsComposeInOrderAndPrependRunsFirst()
    {
        var a = new ProjectiveTransformBuilder().AppendScale(2, 3).AppendTranslation(5, 7).BuildMatrix(50, 40);
        Assert.Equal((2 * 10 + 5, 3 * 20 + 7), (a.Transform(10, 20).X, a.Transform(10, 20).Y));
        var b = new ProjectiveTransformBuilder().AppendScale(2, 3).PrependTranslation(5, 7).BuildMatrix(50, 40);
        Assert.Equal((2 * (10 + 5), 3 * (20 + 7)), (b.Transform(10, 20).X, b.Transform(10, 20).Y));
        // Rotation turns about the center of the image it is built for.
        var r = new ProjectiveTransformBuilder().AppendRotationDegrees(90).BuildMatrix(50, 40);
        (double cx, double cy) = r.Transform(25, 20);
        Assert.Equal((25, 20), (Math.Round(cx, 9), Math.Round(cy, 9)));
        (double x, double y) = r.Transform(50, 20);
        Assert.Equal((25, 45), (Math.Round(x, 9), Math.Round(y, 9)));
        // A builder evaluates against each image's own size.
        var t = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Top, TaperCorner.Both, 0.5);
        Assert.Equal((25.0, 0.0), t.BuildMatrix(100, 60).Transform(0, 0));
        Assert.Equal((12.5, 0.0), (Math.Round(t.BuildMatrix(50, 60).Transform(0, 0).X, 9), t.BuildMatrix(50, 60).Transform(0, 0).Y));
    }

    [Fact]
    public void ImageSharpBuilderGeometryAgrees()
    {
        // Same corner images for tapers, and the same linear part for rotate/scale/skew (ImageSharp also re-anchors the output with a translation).
        static void SameCorners(ProjectiveMatrix ours, System.Numerics.Matrix4x4 theirs, string what)
        {
            foreach ((double x, double y) in new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 60.0), (0.0, 60.0), (37.0, 21.0) })
            {
                System.Numerics.Vector4 v = System.Numerics.Vector4.Transform(new System.Numerics.Vector4((float)x, (float)y, 0, 1), theirs);
                (double ox, double oy) = ours.Transform(x, y);
                Assert.True(Math.Abs(ox - (v.X / v.W)) < 2e-3 && Math.Abs(oy - (v.Y / v.W)) < 2e-3, $"{what} at ({x},{y}): ours ({ox:F3},{oy:F3}) theirs ({v.X / v.W:F3},{v.Y / v.W:F3})");
            }
        }

        foreach (TaperSide side in Enum.GetValues<TaperSide>())
        {
            foreach (TaperCorner corner in Enum.GetValues<TaperCorner>())
            {
                var theirs = new SixLabors.ImageSharp.Processing.ProjectiveTransformBuilder()
                    .AppendTaper(Enum.Parse<SixLabors.ImageSharp.Processing.TaperSide>(side.ToString()), Enum.Parse<SixLabors.ImageSharp.Processing.TaperCorner>(corner.ToString()), 0.6f)
                    .BuildMatrix(new Is.Size(100, 60));
                SameCorners(ProjectiveMatrix.Taper(100, 60, side, corner, 0.6), theirs, $"taper {side} {corner}");
            }
        }

        // Linear part: differences between points must agree.
        var rot = new SixLabors.ImageSharp.Processing.ProjectiveTransformBuilder().AppendRotationDegrees(30).AppendScale(new Is.SizeF(1.5f, 0.75f)).BuildMatrix(new Is.Size(100, 60));
        ProjectiveMatrix mine = new ProjectiveTransformBuilder().AppendRotationDegrees(30).AppendScale(1.5, 0.75).BuildMatrix(100, 60);
        System.Numerics.Vector4 t0 = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(0, 0, 0, 1), rot);
        System.Numerics.Vector4 t1 = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(100, 60, 0, 1), rot);
        (double ax, double ay) = mine.Transform(0, 0);
        (double bx, double by) = mine.Transform(100, 60);
        Assert.Equal(t1.X - t0.X, bx - ax, 2);
        Assert.Equal(t1.Y - t0.Y, by - ay, 2);

        var skew = new SixLabors.ImageSharp.Processing.ProjectiveTransformBuilder().AppendSkewDegrees(20, 10, System.Numerics.Vector2.Zero).BuildMatrix(new Is.Size(100, 60));
        SameCorners(new ProjectiveTransformBuilder().AppendSkewDegrees(20, 10, 0, 0).BuildMatrix(100, 60), skew, "skew");
    }

    // A smooth opaque image, so sub-pixel placement differences between libraries stay small.
    private static Rgba32[] Smooth(int w, int h)
    {
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = new Rgba32(
                    (byte)(40 + (170.0 * x / w)),
                    (byte)(60 + (130.0 * y / h)),
                    (byte)(128 + (90 * Math.Sin((x + y) * 0.08))),
                    255);
            }
        }

        return px;
    }

    [Theory]
    [InlineData("taper-left")]
    [InlineData("taper-top-right")]
    [InlineData("quad")]
    public void CloseToImageSharpsProjectiveTransform(string shape)
    {
        const int W = 160;
        const int H = 120;
        Rgba32[] src = Smooth(W, H);
        var theirsBuilder = new SixLabors.ImageSharp.Processing.ProjectiveTransformBuilder();
        ProjectiveMatrix m;
        switch (shape)
        {
            case "taper-left":
                theirsBuilder.AppendTaper(SixLabors.ImageSharp.Processing.TaperSide.Left, SixLabors.ImageSharp.Processing.TaperCorner.Both, 0.6f);
                m = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0.6).BuildMatrix(W, H);
                break;
            case "taper-top-right":
                theirsBuilder.AppendTaper(SixLabors.ImageSharp.Processing.TaperSide.Top, SixLabors.ImageSharp.Processing.TaperCorner.RightOrBottom, 0.5f);
                m = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Top, TaperCorner.RightOrBottom, 0.5).BuildMatrix(W, H);
                break;
            default:
                // A quad with its minimum corner at the origin, so neither library moves the picture.
                (double X, double Y)[] quad = [(0, 20), (150, 0), (160, 100), (10, 120)];
                // The same map, as a matrix both libraries can take (ImageSharp uses row vectors).
                m = ProjectiveMatrix.FromRectangle(W, H, quad);
                theirsBuilder.AppendMatrix(new System.Numerics.Matrix4x4(
                    (float)m.M11, (float)m.M21, 0, (float)m.M31,
                    (float)m.M12, (float)m.M22, 0, (float)m.M32,
                    0, 0, 1, 0,
                    (float)m.M13, (float)m.M23, 0, (float)m.M33));
                break;
        }

        Image ours = Image.FromPixels<Rgba32>(src, W, H).Perspective(m, filter: ResamplingFilter.CatmullRom, linear: false);

        using var sharp = new Is.Image<IsPx.Rgba32>(W, H);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Rgba32 p = src[(y * W) + x];
                sharp[x, y] = new IsPx.Rgba32(p.R, p.G, p.B, p.A);
            }
        }

        sharp.Mutate(c => c.Transform(theirsBuilder, KnownResamplers.CatmullRom));
        Assert.Equal((sharp.Width, sharp.Height), (ours.Width, ours.Height));

        Rgba32[] o = ours.ToArray<Rgba32>();
        double sum = 0;
        int count = 0;
        int max = 0;
        for (int y = 0; y < ours.Height; y++)
        {
            for (int x = 0; x < ours.Width; x++)
            {
                Rgba32 a = o[(y * ours.Width) + x];
                IsPx.Rgba32 b = sharp[x, y];
                if (a.A < 255 || b.A < 255)
                {
                    continue;
                }

                int d = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                sum += d;
                count++;
                max = Math.Max(max, d);
            }
        }

        Assert.True(count > ours.Width * ours.Height / 3, "most of the output should be inside the quad");
        double mean = sum / count;
        TestContext.Current.SendDiagnosticMessage($"vs ImageSharp {shape}: mean abs diff {mean:F3}, max {max} over {count} opaque pixels");
        Assert.True(mean <= 1.5 && max <= 12, $"{shape}: mean {mean:F3}, max {max}");
    }

    [Fact]
    public void BehindTheHorizonAndSingularMatricesAreTypedErrors()
    {
        Image img = OrientTests.Make(100, 60);
        // w = 1 - 0.02 x crosses zero at x = 50, inside the image.
        Assert.Throws<ImageOperationException>(() => img.Perspective(new ProjectiveMatrix(1, 0, 0, 0, 1, 0, -0.02, 0, 1)).ToArray<Rgba32>());
        // w = 0 at a corner.
        Assert.Throws<ImageOperationException>(() => img.Perspective(new ProjectiveMatrix(1, 0, 0, 0, 1, 0, -0.01, 0, 1)).ToArray<Rgba32>());
        // Singular.
        Assert.Throws<ImageOperationException>(() => img.Perspective(new ProjectiveMatrix(1, 2, 0, 2, 4, 0, 0, 0, 1)).ToArray<Rgba32>());
        Assert.Throws<ImageOperationException>(() => img.Perspective(default(ProjectiveMatrix)).ToArray<Rgba32>());
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Perspective(new ProjectiveMatrix(double.NaN, 0, 0, 0, 1, 0, 0, 0, 1)));
        Assert.Throws<ArgumentException>(() => ProjectiveMatrix.FromRectangle(10, 10, [(0, 0), (10, 0), (5, 0), (0, 10)]));
        Assert.Throws<ArgumentException>(() => ProjectiveMatrix.FromRectangle(10, 10, [(0, 0), (10, 10), (10, 0), (0, 10)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectiveMatrix.Taper(10, 10, TaperSide.Left, TaperCorner.Both, 0));
        // An all-negative w is the same transform as its negation.
        Rgba32[] want = img.Perspective(new ProjectiveMatrix(1.2, 0.1, 3, 0, 1, 2, 0.001, 0, 1)).ToArray<Rgba32>();
        Rgba32[] got = img.Perspective(new ProjectiveMatrix(-1.2, -0.1, -3, 0, -1, -2, -0.001, 0, -1)).ToArray<Rgba32>();
        Assert.Equal(want, got);
    }

    [Fact]
    public void ExtremeTaperStaysBoundedAndExact()
    {
        // 1:50 taper: the far edge is nearly a point, a tile there covers a wide source span but the region stays inside the image.
        const int W = 400;
        const int H = 300;
        Rgba32[] src = Source(W, H, translucent: false);
        var m = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Right, TaperCorner.Both, 0.02).BuildMatrix(W, H);
        (double ox, double oy, int ow, int oh) = ReferencePerspective.Bounds(W, H, m);
        Image r = Image.FromPixels<Rgba32>(src, W, H).Perspective(m, filter: ResamplingFilter.CatmullRom, linear: false);
        Assert.Equal((ow, oh), (r.Width, r.Height));
        double[] want = ReferencePerspective.Run(src, W, H, m, ox, oy, ow, oh, ResamplingFilter.CatmullRom, EdgeMode.Constant, default, false);
        Assert.True(Worst(r.ToArray<Rgba32>(), want) <= 1.0);
    }

    [Fact]
    public void ResultDoesNotDependOnThreadsOrTileOrder()
    {
        Image r = OrientTests.Make(400, 300).Perspective(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Bottom, TaperCorner.Both, 0.5).AppendRotationDegrees(12), filter: ResamplingFilter.Lanczos3);
        Assert.Equal(r.ToArray<Rgba32>(maxDegree: 1), r.ToArray<Rgba32>(maxDegree: 8));
    }

    [Fact]
    public void TilesReadOnlyTheSourceTheyNeed()
    {
        var src = new Resample.RawBytesNode(new byte[3000 * 2000 * 4], 3000, 2000, PixelFormat.Rgba8, cacheable: false);
        Image img = Image.FromNode(src).Perspective(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0.7), filter: ResamplingFilter.Lanczos3);
        Node node = img.Frames[0].Node;
        ImageRect tile = node.TileRect(10, 8);
        ImageRect need = node.RequiredInput(0, tile);
        Assert.False(need.IsEmpty);
        Assert.True((long)need.Width * need.Height < 4L * tile.Width * tile.Height, $"need {need} for tile {tile}");
        img.CopyPixelsTo<Rgba32>(new Rgba32[tile.Width * tile.Height], tile, maxDegree: 1);
        long tiles = ((((need.Right - 1) >> 7) - (need.X >> 7)) + 1) * ((((need.Bottom - 1) >> 7) - (need.Y >> 7)) + 1);
        Assert.Equal(tiles, src.Computed);
    }

    [Fact]
    public void FramesOfAnAnimationEachGetTheirOwnMatrixFromTheBuilder()
    {
        Image a = OrientTests.Make(60, 40);
        Image b = OrientTests.Make(30, 20);
        var builder = new ProjectiveTransformBuilder().AppendTaper(TaperSide.Top, TaperCorner.Both, 0.5);
        Assert.Equal(60, a.Perspective(builder).Width);
        Assert.Equal(30, b.Perspective(builder).Width);
    }
}
