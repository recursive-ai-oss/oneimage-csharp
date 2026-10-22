using System.Diagnostics;
using System.Numerics;
using OneImage.Drawing;
using Xunit;
using IntersectionRule = OneImage.Drawing.IntersectionRule;
using Path = OneImage.Drawing.Path;
using PathBuilder = OneImage.Drawing.PathBuilder;

namespace OneImage.Tests.Drawing;

public class FillTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);

    private static Path Scene() => Path.Combine(
        Path.Circle(150, 120, 90),
        Path.Star(200, 150, 6, 30, 80, 10),
        Path.Rectangle(100.4f, 20.3f, 160.25f, 33.6f),
        Path.ParseSvg("M10 200 C 60 120 120 300 290 230 L 270 250 Q 150 280 40 245 Z"));

    private static Rgba32[] Pattern(int w, int h, bool opaque)
    {
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = new Rgba32((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x ^ y) * 3), opaque ? (byte)255 : (byte)(40 + ((x + y) % 200)));
            }
        }

        return px;
    }

    private static Canvas BaseCanvas(PixelFormat format, int w = 300, int h = 260)
    {
        var c = new Canvas(w, h, format);
        c.CopyFrom<Rgba32>(Pattern(w, h, !format.HasAlpha), new ImageRect(0, 0, w, h));
        return c;
    }

    [Fact]
    public void CoverageIsTheExactAreaFraction()
    {
        // x 2.25..6.75, y 3.5..5.75 on a transparent image: alpha = 255 · (covered width) · (covered height).
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[10 * 10], 10, 10).Fill(White, Path.Rectangle(2.25f, 3.5f, 4.5f, 2.25f)).ToArray<Rgba32>();
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                double cx = Math.Max(0, Math.Min(x + 1, 6.75) - Math.Max(x, 2.25));
                double cy = Math.Max(0, Math.Min(y + 1, 5.75) - Math.Max(y, 3.5));
                Assert.InRange(px[(y * 10) + x].A, Math.Round(255 * cx * cy) - 1, Math.Round(255 * cx * cy) + 1);
            }
        }
    }

    [Fact]
    public void PartialCoverageBlendsOverAnOpaqueBackdrop()
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(0, 0, 255, 255), 100).ToArray(), 10, 10)
            .Fill(new Rgba32(255, 0, 0, 255), Path.Rectangle(2.5f, 0, 4, 10)).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[0]);
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[4]);
        Assert.InRange(px[2].R, 127, 129); // half covered
        Assert.InRange(px[2].B, 127, 128);
        Assert.Equal(255, px[2].A);
    }

    [Fact]
    public void FillOnRgbAndGrayKeepsFormat()
    {
        Image rgb = Image.FromPixels<Rgba32>(Pattern(64, 64, true), 64, 64).ConvertTo(PixelFormat.Rgb8).Fill(new Rgba32(10, 200, 30, 255), Path.Circle(32, 32, 20));
        Assert.Equal(PixelFormat.Rgb8, rgb.Format);
        Rgba32 c = rgb.ToArray<Rgba32>()[(32 * 64) + 32];
        Assert.Equal(new Rgba32(10, 200, 30, 255), c);
        Image gray = Image.FromPixels<Rgba32>(Pattern(64, 64, true), 64, 64).ConvertTo(PixelFormat.Gray8).Fill(new Rgba32(255, 255, 255, 255), Path.Circle(32, 32, 20));
        Assert.Equal(PixelFormat.Gray8, gray.Format);
        Assert.Equal(255, gray.ToArray<Rgba32>()[(32 * 64) + 32].R);
    }

    public static TheoryData<string> Formats() => ["Rgba8", "Rgb8", "Gray8", "GrayAlpha8", "Rgba16", "RgbaF32"];

    private static PixelFormat Named(string n) => n switch
    {
        "Rgba8" => PixelFormat.Rgba8,
        "Rgb8" => PixelFormat.Rgb8,
        "Gray8" => PixelFormat.Gray8,
        "GrayAlpha8" => PixelFormat.GrayAlpha8,
        "Rgba16" => PixelFormat.Rgba16,
        _ => PixelFormat.RgbaF32,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void CanvasAndImageProduceTheSameBytes(string name)
    {
        PixelFormat format = Named(name);
        (bool Aa, IntersectionRule Rule, BlendMode Blend, AlphaComposition Comp, float Opacity, bool Linear)[] cases =
        [
            (true, IntersectionRule.NonZero, BlendMode.Normal, AlphaComposition.SrcOver, 1f, false),
            (true, IntersectionRule.EvenOdd, BlendMode.Normal, AlphaComposition.SrcOver, 0.6f, false),
            (false, IntersectionRule.NonZero, BlendMode.Normal, AlphaComposition.SrcOver, 1f, false),
            (true, IntersectionRule.NonZero, BlendMode.Multiply, AlphaComposition.SrcOver, 0.8f, false),
            (true, IntersectionRule.NonZero, BlendMode.Screen, AlphaComposition.SrcAtop, 1f, false),
            (true, IntersectionRule.NonZero, BlendMode.Normal, AlphaComposition.SrcOver, 0.7f, true),
            (true, IntersectionRule.NonZero, BlendMode.Overlay, AlphaComposition.SrcOver, 1f, true),
            (true, IntersectionRule.NonZero, BlendMode.Normal, AlphaComposition.Src, 1f, false),
            (true, IntersectionRule.NonZero, BlendMode.Normal, AlphaComposition.Clear, 1f, false),
        ];
        foreach (var k in cases)
        {
            if (!format.HasAlpha && !(k.Comp is AlphaComposition.SrcOver or AlphaComposition.SrcAtop))
            {
                continue;
            }

            var color = new Rgba32(220, 90, 30, format.HasAlpha ? (byte)200 : (byte)255);
            using Canvas a = BaseCanvas(format);
            Image viaImage = a.Snapshot().Fill(color, Scene(), k.Aa, k.Rule, k.Opacity, k.Blend, k.Comp, k.Linear);
            using Canvas b = BaseCanvas(format);
            b.Fill(color, Scene(), k.Aa, k.Rule, k.Opacity, k.Blend, k.Comp, k.Linear);
            Assert.Equal(viaImage.ToArray<Rgba32>(), ToRgba(b));
        }
    }

    private static Rgba32[] ToRgba(Canvas c)
    {
        var px = new Rgba32[c.Width * c.Height];
        c.CopyTo<Rgba32>(px, new ImageRect(0, 0, c.Width, c.Height));
        return px;
    }

    [Fact]
    public void ResultIsIdenticalAtAnyDegree()
    {
        using Canvas c = BaseCanvas(PixelFormat.Rgba8);
        Image img = c.Snapshot().Fill(new Rgba32(10, 20, 250, 180), Scene(), true, IntersectionRule.EvenOdd, 0.9f);
        Rgba32[] one = img.ToArray<Rgba32>(maxDegree: 1);
        Assert.Equal(one, c.Snapshot().Fill(new Rgba32(10, 20, 250, 180), Scene(), true, IntersectionRule.EvenOdd, 0.9f).ToArray<Rgba32>(maxDegree: 8));
    }

    [Theory]
    [InlineData(0, 0, 300, 260)]
    [InlineData(128, 0, 128, 128)]
    [InlineData(100, 90, 17, 60)]
    [InlineData(255, 129, 45, 131)]
    [InlineData(0, 255, 300, 5)]
    public void ARegionEqualsTheSameRegionOfTheWhole(int x, int y, int w, int h)
    {
        using Canvas c = BaseCanvas(PixelFormat.Rgba8);
        Image img = c.Snapshot().Fill(new Rgba32(10, 220, 50, 200), Scene(), true, IntersectionRule.NonZero, 0.8f, BlendMode.Multiply);
        Rgba32[] whole = img.ToArray<Rgba32>();
        var region = new Rgba32[w * h];
        c.Snapshot().Fill(new Rgba32(10, 220, 50, 200), Scene(), true, IntersectionRule.NonZero, 0.8f, BlendMode.Multiply).CopyPixelsTo<Rgba32>(region, new ImageRect(x, y, w, h));
        for (int r = 0; r < h; r++)
        {
            Assert.Equal(whole.AsSpan(((y + r) * 300) + x, w).ToArray(), region.AsSpan(r * w, w).ToArray());
        }
    }

    [Fact]
    public void TilesTheShapeDoesNotReachAreTheInputsTiles()
    {
        using Canvas c = BaseCanvas(PixelFormat.Rgba8, 600, 600);
        Image baseImage = c.Snapshot();
        Image filled = baseImage.Fill(White, Path.Circle(40, 40, 20));
        Rgba32[] a = baseImage.ToArray<Rgba32>(), b = filled.ToArray<Rgba32>();
        for (int y = 0; y < 600; y++)
        {
            for (int x = 128; x < 600; x++)
            {
                Assert.Equal(a[(y * 600) + x], b[(y * 600) + x]);
            }
        }

        Assert.NotEqual(a[(40 * 600) + 40], b[(40 * 600) + 40]);
    }

    [Fact]
    public void LinearLightBlendsHalfCoverageBrighter()
    {
        Image black = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(0, 0, 0, 255), 100).ToArray(), 10, 10);
        Path half = Path.Rectangle(0, 0, 10, 5.5f); // row 5 is half covered
        byte encoded = black.Fill(White, half).ToArray<Rgba32>()[(5 * 10) + 3].R;
        byte linear = black.Fill(White, half, linear: true).ToArray<Rgba32>()[(5 * 10) + 3].R;
        Assert.InRange(encoded, 127, 129);
        Assert.InRange(linear, 186, 190); // sRGB(0.5 linear) = 188
        Assert.Equal(255, black.Fill(White, half, linear: true).ToArray<Rgba32>()[3].R);
        Assert.Equal(0, black.Fill(White, half, linear: true).ToArray<Rgba32>()[(8 * 10) + 3].R);
        // A gray image goes through the gray profile.
        byte gray = black.ConvertTo(PixelFormat.Gray8).Fill(White, half, linear: true).ToArray<Rgba32>()[(5 * 10) + 3].R;
        Assert.InRange(gray, 186, 190);
    }

    [Fact]
    public void EveryFrameOfAnAnimationIsFilled()
    {
        Node f0 = new Resample.RawBytesNode(new byte[64 * 64 * 3], 64, 64, PixelFormat.Rgb8);
        Node f1 = new Resample.RawBytesNode(new byte[32 * 32 * 3], 32, 32, PixelFormat.Rgb8);
        var anim = new Image([f0, f1], [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80)], 3, ImageMetadata.Empty, null);
        Image r = anim.Fill(new Rgba32(255, 0, 0, 255), Path.Rectangle(8, 8, 20, 20));
        Assert.Equal(2, r.FrameCount);
        Assert.Equal(new Rgba32(255, 0, 0, 255), r.Frames[0].ToArray<Rgba32>()[(10 * 64) + 10]);
        Assert.Equal(new Rgba32(255, 0, 0, 255), r.Frames[1].ToArray<Rgba32>()[(10 * 32) + 10]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), r.Frames[1].ToArray<Rgba32>()[(30 * 32) + 30]);
        Assert.Equal(TimeSpan.FromMilliseconds(80), r.Frames[1].Delay);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image img = Image.FromPixels<Rgba32>(new Rgba32[100], 10, 10);
        Path p = Path.Circle(5, 5, 3);
        Assert.Throws<ArgumentNullException>(() => img.Fill((Brush)null!, p));
        Assert.Throws<ArgumentNullException>(() => img.Fill(White, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Fill(White, p, opacity: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Fill(White, p, opacity: 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Fill(White, p, rule: (IntersectionRule)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Fill(White, p, blend: (BlendMode)200));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Fill(White, p, transform: new Matrix3x2(float.NaN, 0, 0, 1, 0, 0)));
        Image rgb = img.ConvertTo(PixelFormat.Rgb8);
        Assert.Throws<ImageOperationException>(() => rgb.Fill(White, p, composition: AlphaComposition.Clear));
        Assert.Throws<ImageOperationException>(() => img.ConvertTo(PixelFormat.Cmyk8).Fill(White, p));
        using var canvas = new Canvas(10, 10, PixelFormat.Cmyk8);
        Assert.Throws<ImageOperationException>(() => canvas.Fill(White, p));
        var disposed = new Canvas(10, 10, PixelFormat.Rgba8);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.Fill(White, p));
    }

    [Fact]
    public void ClearAndWholeImageFill()
    {
        Image img = Image.FromPixels<Rgba32>(Pattern(40, 40, false), 40, 40);
        Assert.All(img.Clear(new Rgba32(1, 2, 3, 4)).ToArray<Rgba32>(), p => Assert.Equal(new Rgba32(1, 2, 3, 4), p));
        Assert.All(img.Fill(new Rgba32(9, 9, 9, 255)).ToArray<Rgba32>(), p => Assert.Equal(new Rgba32(9, 9, 9, 255), p));
        // Without alpha a clear is a flattening fill: a translucent color mixes with what was there.
        Image rgb = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(100, 100, 100, 255), 16).ToArray(), 4, 4).ConvertTo(PixelFormat.Rgb8);
        Assert.All(rgb.Clear(new Rgba32(200, 200, 200, 128)).ToArray<Rgba32>(), p => Assert.InRange(p.R, 149, 151));
        using var canvas = new Canvas(300, 200, PixelFormat.Rgba8);
        canvas.Clear(new Rgba32(7, 8, 9, 255));
        Assert.Equal(new Rgba32(7, 8, 9, 255), canvas[299, 199]);
        Assert.Equal(new Rgba32(7, 8, 9, 255), canvas[0, 0]);
    }

    [Fact]
    public void FillMatchesCompositeForABlendMode()
    {
        Rgba32[] baseline = Pattern(64, 64, false);
        var overlayColor = new Rgba32(90, 180, 40, 160);
        Image b = Image.FromPixels<Rgba32>(baseline, 64, 64);
        Image viaFill = b.Fill(overlayColor, Path.Rectangle(0, 0, 64, 64), false, blend: BlendMode.Multiply, opacity: 0.75f);
        Image viaComposite = b.Composite(Image.FromPixels<Rgba32>(Enumerable.Repeat(overlayColor, 64 * 64).ToArray(), 64, 64), 0, 0, 0.75f, false, BlendMode.Multiply, AlphaComposition.SrcOver);
        Assert.Equal(viaComposite.ToArray<Rgba32>(), viaFill.ToArray<Rgba32>());
    }

    // A brush that is not recognized as solid takes the row-by-row path; for a plain color it must agree with the solid fast path.
    private sealed class RowBrush(Rgba32 color) : Brush
    {
        public override bool TryGetSolid(out Rgba32 c)
        {
            c = default;
            return false;
        }

        public override void FillRow(int x, int y, Span<float> rgba)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = color.R / 255f;
                rgba[i + 1] = color.G / 255f;
                rgba[i + 2] = color.B / 255f;
                rgba[i + 3] = color.A / 255f;
            }
        }

        public override IReadOnlyList<OpParameter> Describe() => [new("brush", "row")];
    }

    [Fact]
    public void RowBrushPathAgreesWithTheSolidFastPath()
    {
        var color = new Rgba32(30, 140, 220, 190);
        Image b = Image.FromPixels<Rgba32>(Pattern(300, 260, false), 300, 260);
        Rgba32[] solid = b.Fill(color, Scene()).ToArray<Rgba32>();
        Rgba32[] rows = b.Fill(new RowBrush(color), Scene()).ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < solid.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(solid[i].R - rows[i].R), Math.Abs(solid[i].G - rows[i].G)), Math.Max(Math.Abs(solid[i].B - rows[i].B), Math.Abs(solid[i].A - rows[i].A))));
        }

        Assert.True(worst <= 1, $"worst difference {worst}");

        // In linear light a row brush is converted after the call and must agree with the solid fill, which converts its color once.
        Rgba32[] solidLinear = b.Fill(color, Scene(), linear: true).ToArray<Rgba32>();
        Rgba32[] rowsLinear = b.Fill(new RowBrush(color), Scene(), linear: true).ToArray<Rgba32>();
        int worstLinear = 0;
        for (int i = 0; i < solidLinear.Length; i++)
        {
            worstLinear = Math.Max(worstLinear, Math.Max(Math.Max(Math.Abs(solidLinear[i].R - rowsLinear[i].R), Math.Abs(solidLinear[i].G - rowsLinear[i].G)), Math.Max(Math.Abs(solidLinear[i].B - rowsLinear[i].B), Math.Abs(solidLinear[i].A - rowsLinear[i].A))));
        }

        Assert.True(worstLinear <= 1, $"worst linear difference {worstLinear}");
    }

    [Fact]
    public void ChainedFillsEqualFillsOneAtATime()
    {
        Rgba32[] start = Pattern(300, 260, false);
        Image chained = Image.FromPixels<Rgba32>(start, 300, 260);
        Rgba32[] stepwise = start;
        for (int i = 0; i < 12; i++)
        {
            Path p = Path.Circle(20 + (i * 23), 30 + (i * 17), 25 + i);
            var color = new Rgba32((byte)(i * 20), (byte)(255 - (i * 15)), 90, (byte)(120 + (i * 10)));
            chained = chained.Fill(color, p, opacity: 0.9f);
            stepwise = Image.FromPixels<Rgba32>(stepwise, 300, 260).Fill(color, p, opacity: 0.9f).ToArray<Rgba32>();
        }

        Assert.Equal(stepwise, chained.ToArray<Rgba32>());
    }

    [Fact]
    public void TransformMovesTheShape()
    {
        Image img = Image.FromPixels<Rgba32>(new Rgba32[50 * 50], 50, 50);
        Rgba32[] moved = img.Fill(White, Path.Rectangle(0, 0, 10, 10), transform: Matrix3x2.CreateTranslation(20, 30)).ToArray<Rgba32>();
        Assert.Equal(255, moved[(35 * 50) + 25].A);
        Assert.Equal(0, moved[(5 * 50) + 5].A);
    }

    // ---- Hostile input ----

    [Fact]
    public void DegeneratePathsChangeNothing()
    {
        Image img = Image.FromPixels<Rgba32>(Pattern(40, 40, false), 40, 40);
        Rgba32[] before = img.ToArray<Rgba32>();
        foreach (Path p in new[]
        {
            Path.Empty,
            Path.Polygon([new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5)]),
            Path.Polygon([new Vector2(1, 1), new Vector2(30, 30), new Vector2(15, 15)]), // collinear
            Path.Rectangle(10, 10, 0, 20),
            Path.Rectangle(-50, -50, 20, 20), // outside
            Path.Rectangle(100, 5, 20, 20),
            Path.Circle(10, 10, 0),
        })
        {
            Assert.Equal(before, img.Fill(White, p).ToArray<Rgba32>());
        }
    }

    [Fact]
    public void HugeAndFarCoordinatesRenderSanely()
    {
        Image img = Image.FromPixels<Rgba32>(new Rgba32[64 * 64], 64, 64);
        // A rectangle that contains the whole image, with enormous corners, covers everything.
        Rgba32[] all = img.Fill(White, Path.Rectangle(-3e30f, -3e30f, 6e30f, 6e30f)).ToArray<Rgba32>();
        Assert.All(all, p => Assert.Equal(White, p));
        // A huge triangle (coordinates still exact in float): the part inside the image is rendered, the rest ignored.
        Path tri = Path.Polygon([new Vector2(-1e6f, -1e6f), new Vector2(1e6f, 1e6f), new Vector2(-1e6f, 1e6f)]);
        Rgba32[] half = img.Fill(White, tri).ToArray<Rgba32>();
        Assert.Equal(255, half[(60 * 64) + 3].A);
        Assert.Equal(0, half[(3 * 64) + 60].A);
        // Curves with huge control points are culled or subdivided within bounds: this must finish quickly.
        var sw = Stopwatch.StartNew();
        Path wild = new PathBuilder().MoveTo(0, 0).CubicBezierTo(1e9f, -1e9f, -1e9f, 1e9f, 60, 60).CubicBezierTo(1e30f, 0, 0, 1e30f, 0, 60).Close().Build();
        _ = img.Fill(White, wild).ToArray<Rgba32>();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact]
    public void AMillionSegmentsFinishAndRenderCorrectly()
    {
        const int n = 2_000_000;
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n;
            double r = 900 + (100 * Math.Sin(a * 50));
            pts[i] = new Vector2((float)(1000 + (r * Math.Cos(a))), (float)(1000 + (r * Math.Sin(a))));
        }

        var sw = Stopwatch.StartNew();
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[2000 * 2000], 2000, 2000).Fill(White, Path.Polygon(pts)).ToArray<Rgba32>();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
        Assert.Equal(255, px[(1000 * 2000) + 1000].A);
        Assert.Equal(0, px[0].A);
    }

    [Fact]
    public void TooManyFlattenedPointsFailTypedWhenPixelsAreComputed()
    {
        // 2^24 + 1 points: the path builds (it is only memory), the fill is refused with a limit error when computed.
        int n = Path.MaxFlattenedPoints + 1;
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            pts[i] = new Vector2(i & 1023, (i >> 10) & 1023);
        }

        Image img = Image.FromPixels<Rgba32>(new Rgba32[16], 4, 4).Fill(White, Path.Polygon(pts)); // lazy: no error yet
        Assert.Throws<ImageLimitException>(() => img.ToArray<Rgba32>());
    }

    [Fact]
    public void ATransformThatOverflowsFailsTyped()
    {
        Image img = Image.FromPixels<Rgba32>(new Rgba32[16], 4, 4);
        Image r = img.Fill(White, Path.Rectangle(1e30f, 1e30f, 5, 5), transform: Matrix3x2.CreateScale(1e30f));
        Assert.Throws<ImageOperationException>(() => r.ToArray<Rgba32>());
        Assert.Throws<ImageOperationException>(() => Path.Rectangle(1e30f, 0, 1, 1).Scale(1e30f, 1));
    }

    [Fact]
    public void CancellationStopsAFill()
    {
        Image img = Image.FromPixels<Rgba32>(new Rgba32[1000 * 1000], 1000, 1000).Fill(White, Path.Circle(500, 500, 400));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => img.ToArray<Rgba32>(cancellationToken: cts.Token));
    }
}
