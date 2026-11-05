using System.Reflection;
using OneImage.Text;

namespace OneImage.Metafile.Tests;

public sealed class MetafileRenderTests
{
    internal static readonly string RepoRoot = typeof(MetafileRenderTests).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;

    internal static MetafileFontOptions TestFonts()
    {
        var fc = new FontCollection();
        fc.Add(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSans-Subset-Regular.ttf"));
        fc.Add(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSans-Subset-Bold-kern.ttf"));
        return new MetafileFontOptions { Fonts = fc, UseSystemFonts = false, DefaultFamily = fc.First().Name };
    }

    internal static (Rgba32[] Pixels, int Width, int Height) Render(byte[] emf, int? width = null, bool antialias = false, MetafileFontOptions? fonts = null)
    {
        var config = new ImageConfiguration().Add(new MetafileFormat(fonts: fonts, antialias: antialias));
        Image img = Image.Load(emf, new LoadOptions { Configuration = config });
        if (width is { } w)
        {
            img = img.Resize(width: w);
        }

        return (img.ToArray<Rgba32>(), img.Width, img.Height);
    }

    private static Rgba32 At((Rgba32[] P, int W, int H) r, int x, int y) => r.P[(y * r.W) + x];

    private static readonly int Red = EmfBuilder.Rgb(255, 0, 0);
    private static readonly int Blue = EmfBuilder.Rgb(0, 0, 255);

    private static void Is(Rgba32 c, byte r, byte g, byte b, byte a = 255) => Assert.Equal(new Rgba32(r, g, b, a), c);

    private static void Clear(Rgba32 c) => Assert.Equal(0, c.A);

    [Fact]
    public void LoadsWithTheNaturalSizeOfTheFrame()
    {
        byte[] emf = new EmfBuilder(100).Rectangle(1, 1, 5, 5).Build();
        var config = new ImageConfiguration().Add(MetafileFormat.Instance);
        ImageInfo info = Image.Identify(emf, new LoadOptions { Configuration = config });
        Assert.Equal(100, info.Width);
        Assert.Equal(100, info.Height);
        Assert.Equal("emf", info.Format.Name);
    }

    [Fact]
    public void RectangleHasABorderOfPenWidthAndExcludesRightAndBottom()
    {
        var emf = new EmfBuilder().CreatePen(1, 0, 1, Blue).CreateBrush(2, 0, Red).Select(1).Select(2).Rectangle(10, 10, 50, 40).Build();
        var r = Render(emf);
        Is(At(r, 30, 25), 255, 0, 0);
        Is(At(r, 10, 25), 0, 0, 255);
        Is(At(r, 49, 25), 0, 0, 255);
        Is(At(r, 30, 10), 0, 0, 255);
        Is(At(r, 30, 39), 0, 0, 255);
        Clear(At(r, 50, 25));
        Clear(At(r, 30, 40));
        Clear(At(r, 9, 25));
    }

    [Fact]
    public void NullPenFillsTheWholeBox()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Rectangle(20, 20, 30, 30).Build();
        var r = Render(emf);
        Is(At(r, 20, 20), 255, 0, 0);
        Is(At(r, 29, 29), 255, 0, 0);
        Clear(At(r, 30, 29));
        Clear(At(r, 19, 20));
    }

    [Fact]
    public void ReversedCornersDrawTheSameRectangle()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Rectangle(30, 30, 20, 20).Build();
        Is(At(Render(emf), 25, 25), 255, 0, 0);
    }

    [Fact]
    public void LineToDrawsOnePixelLineThroughPixelCenters()
    {
        var emf = new EmfBuilder().CreatePen(1, 0, 1, Blue).Select(1).MoveTo(10, 20).LineTo(60, 20).Build();
        var r = Render(emf);
        Is(At(r, 30, 20), 0, 0, 255);
        Clear(At(r, 30, 19));
        Clear(At(r, 30, 21));
    }

    [Fact]
    public void WidePenIsCenteredOnTheLine()
    {
        var emf = new EmfBuilder().CreatePen(1, 0, 5, Blue).Select(1).MoveTo(10, 50).LineTo(90, 50).Build();
        var r = Render(emf);
        for (int y = 48; y <= 52; y++)
        {
            Is(At(r, 50, y), 0, 0, 255);
        }

        Clear(At(r, 50, 47));
        Clear(At(r, 50, 53));
    }

    [Fact]
    public void PolygonFillModesTreatTheHoleDifferently()
    {
        int[] outer = [10, 10, 90, 10, 90, 90, 10, 90];
        int[] inner = [30, 30, 70, 30, 70, 70, 30, 70];
        int[] pts = [.. outer, .. inner];
        var alt = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).SetPolyFillMode(1).PolyPolygon(8, [4, 4], pts).Build();
        var win = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).SetPolyFillMode(2).PolyPolygon(8, [4, 4], pts).Build();
        Clear(At(Render(alt), 50, 50));
        Is(At(Render(alt), 20, 50), 255, 0, 0);
        Is(At(Render(win), 50, 50), 255, 0, 0);
    }

    [Fact]
    public void SixteenBitPolygonRecordsWork()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Polygon(86, true, 10, 10, 60, 10, 60, 60, 10, 60).Build();
        Is(At(Render(emf), 30, 30), 255, 0, 0);
    }

    [Fact]
    public void EllipseCoversItsCenterAndNotItsCorners()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Ellipse(10, 10, 90, 90).Build();
        var r = Render(emf);
        Is(At(r, 50, 50), 255, 0, 0);
        Clear(At(r, 12, 12));
        Is(At(r, 50, 12), 255, 0, 0);
    }

    [Fact]
    public void PieAndChordCloseTheArc()
    {
        // Counterclockwise from the right (90,50) to the top (50,10): a quarter in the upper right.
        var pie = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Arc(47, 10, 10, 90, 90, 90, 50, 50, 10).Build();
        var r = Render(pie);
        Is(At(r, 65, 35), 255, 0, 0);
        Clear(At(r, 35, 65));
        Clear(At(r, 35, 35));
    }

    [Fact]
    public void BezierCurveIsStrokedAndNotFilled()
    {
        var emf = new EmfBuilder().CreatePen(1, 0, 1, Blue).CreateBrush(2, 0, Red).Select(1).Select(2).Polygon(2, false, 10, 80, 10, 10, 90, 10, 90, 80).Build();
        var r = Render(emf);
        Clear(At(r, 50, 70));
        int blue = 0;
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                if (At(r, x, y).B == 255 && At(r, x, y).A == 255)
                {
                    blue++;
                }
            }
        }

        Assert.True(blue > 100);
    }

    [Fact]
    public void PathBracketFillsAndStrokes()
    {
        var emf = new EmfBuilder().CreatePen(1, 0, 3, Blue).CreateBrush(2, 0, Red).Select(1).Select(2)
            .BeginPath().Rectangle(20, 20, 70, 60).EndPath().StrokeAndFillPath().Build();
        var r = Render(emf);
        Is(At(r, 45, 40), 255, 0, 0);
        Is(At(r, 20, 40), 0, 0, 255);
        Clear(At(r, 15, 40));
    }

    [Fact]
    public void ClipPathLimitsLaterDrawing()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1)
            .BeginPath().Ellipse(0, 0, 100, 100).EndPath().SelectClipPath(5).Rectangle(0, 0, 100, 100).Build();
        var r = Render(emf);
        Is(At(r, 50, 50), 255, 0, 0);
        Clear(At(r, 3, 3));
    }

    [Fact]
    public void ClipRectanglesIntersectAndExclude()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1)
            .IntersectClipRect(0, 0, 50, 100).ExcludeClipRect(20, 20, 40, 40).Rectangle(0, 0, 100, 100).Build();
        var r = Render(emf);
        Is(At(r, 10, 10), 255, 0, 0);
        Clear(At(r, 30, 30));
        Clear(At(r, 60, 10));
    }

    [Fact]
    public void SaveAndRestoreBringBackPenBrushAndClip()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).CreateBrush(2, 0, Blue).Select(1)
            .SaveDc().Select(2).IntersectClipRect(0, 0, 10, 10).RestoreDc(-1).Rectangle(40, 40, 60, 60).Build();
        Is(At(Render(emf), 50, 50), 255, 0, 0);
    }

    [Fact]
    public void LowEnglishMapModeCountsInHundredthsOfAnInchWithYUp()
    {
        // 0.01 inch is 1.016 pixel on a 4 px/mm device; y grows upward from the origin at the top left, so a negative y lies below it.
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).MapMode(4).Rectangle(0, 0, 50, -50).Build();
        var r = Render(emf);
        Is(At(r, 50, 50), 255, 0, 0);
        Clear(At(r, 52, 52));
    }

    [Fact]
    public void AnisotropicExtentsScaleAndOriginsMove()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).MapMode(8).WindowExt(10, 10).ViewportExt(100, 100).WindowOrg(-1, -1).Rectangle(0, 0, 3, 3).Build();
        var r = Render(emf);
        Is(At(r, 15, 15), 255, 0, 0);
        Clear(At(r, 5, 5));
        Clear(At(r, 45, 45));
    }

    [Fact]
    public void WorldTransformMovesAndScales()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).SetWorldTransform(2, 0, 0, 2, 10, 20).Rectangle(0, 0, 10, 10).Build();
        var r = Render(emf);
        Is(At(r, 25, 35), 255, 0, 0);
        Clear(At(r, 9, 35));
        Clear(At(r, 31, 35));
    }

    [Fact]
    public void StretchDibitsPlacesAndStretchesAThreeChannelBitmap()
    {
        var emf = new EmfBuilder().StretchDibits(10, 10, 40, 40, 2, 2, (x, y) => (x, y) switch { (0, 0) => ((byte)255, (byte)0, (byte)0), (1, 0) => ((byte)0, (byte)255, (byte)0), (0, 1) => ((byte)0, (byte)0, (byte)255), _ => ((byte)255, (byte)255, (byte)255) }).Build();
        var r = Render(emf);
        Is(At(r, 15, 15), 255, 0, 0);
        Is(At(r, 40, 15), 0, 255, 0);
        Is(At(r, 15, 40), 0, 0, 255);
        Is(At(r, 40, 40), 255, 255, 255);
        Clear(At(r, 9, 15));
    }

    [Fact]
    public void PatBltDestinationInvertTurnsRedIntoCyan()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Rectangle(0, 0, 100, 100).PatBlt(10, 10, 30, 30, 0x00550009).Build();
        var r = Render(emf);
        Is(At(r, 20, 20), 0, 255, 255);
        Is(At(r, 60, 60), 255, 0, 0);
    }

    [Fact]
    public void BlacknessAndWhitenessFillSolid()
    {
        var emf = new EmfBuilder().PatBlt(0, 0, 10, 10, 0x00000042).PatBlt(20, 0, 10, 10, 0x00FF0062).Build();
        var r = Render(emf);
        Is(At(r, 5, 5), 0, 0, 0);
        Is(At(r, 25, 5), 255, 255, 255);
    }

    [Fact]
    public void XorPenInvertsWhatIsUnderIt()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).SetRop2(6).Rectangle(10, 10, 30, 30).Build();
        // R2_NOT inverts the destination; transparent pixels count as white, so black results.
        Is(At(Render(emf), 20, 20), 0, 0, 0);
    }

    [Fact]
    public void HatchBrushDrawsLinesEveryEightPixels()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 2, EmfBuilder.Rgb(0, 0, 0), 0).Select(1).SetBkMode(2).SetBkColor(EmfBuilder.Rgb(255, 255, 255)).Rectangle(0, 0, 40, 40).Build();
        var r = Render(emf);
        Is(At(r, 10, 4), 0, 0, 0);
        Is(At(r, 10, 12), 0, 0, 0);
        Is(At(r, 10, 5), 255, 255, 255);
    }

    [Fact]
    public void TransparentHatchLeavesTheGapsAlone()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 2, EmfBuilder.Rgb(0, 0, 0), 1).Select(1).SetBkMode(1).Rectangle(0, 0, 40, 40).Build();
        var r = Render(emf);
        Is(At(r, 4, 10), 0, 0, 0);
        Clear(At(r, 5, 10));
    }

    [Fact]
    public void GradientFillBlendsAcrossTheRectangle()
    {
        var emf = new EmfBuilder().GradientRect(0, 0, 100, 100, [0, 0, 0, 0], [0xFF00, 0xFF00, 0xFF00, 0], 0).Build();
        var r = Render(emf);
        Assert.InRange(At(r, 50, 50).R, 120, 135);
        Assert.InRange(At(r, 5, 50).R, 0, 20);
        Assert.InRange(At(r, 95, 50).R, 235, 255);
        Assert.Equal(At(r, 50, 10).R, At(r, 50, 90).R);
    }

    [Fact]
    public void TextIsDrawnFromTheFontAndFollowsAlignmentAndEscapement()
    {
        MetafileFontOptions fonts = TestFonts();
        byte[] left = new EmfBuilder().CreateFont(1, -30, "Arial").Select(1).SetBkMode(1).ExtTextOutW("HH", 10, 10).Build();
        byte[] right = new EmfBuilder().CreateFont(1, -30, "Arial").Select(1).SetBkMode(1).SetTextAlign(2).ExtTextOutW("HH", 90, 10).Build();
        var l = Render(left, fonts: fonts);
        var rt = Render(right, fonts: fonts);
        (int x0, int x1, int y0, int y1) = Ink(l);
        Assert.True(x0 >= 9 && x1 < 60, $"{x0}..{x1}");
        Assert.InRange(y0, 9, 20);
        Assert.InRange(y1, 25, 45);
        (int rx0, int rx1, _, _) = Ink(rt);
        Assert.True(rx1 <= 91 && rx0 > 50, $"{rx0}..{rx1}");
    }

    [Fact]
    public void RotatedTextTurnsAroundItsReferencePoint()
    {
        MetafileFontOptions fonts = TestFonts();
        byte[] up = new EmfBuilder().CreateFont(1, -30, "Arial", escapement: 900).Select(1).SetBkMode(1).SetTextAlign(24).ExtTextOutW("HH", 20, 80).Build();
        var r = Render(up, fonts: fonts);
        (int x0, int x1, int y0, int y1) = Ink(r);
        Assert.True(y1 - y0 > x1 - x0, "text turned upward is taller than wide");
        Assert.True(y1 <= 81 && x0 >= 20 - 31, $"{x0}..{x1} {y0}..{y1}");
    }

    [Fact]
    public void ImageLoadThenResizeThenSaveWorks()
    {
        var emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Rectangle(0, 0, 50, 100).Build();
        var config = new ImageConfiguration().Add(MetafileFormat.Instance);
        using var ms = new MemoryStream();
        Image.Load(emf, new LoadOptions { Configuration = config }).Resize(width: 40).Save(ms, PngFormat.Instance);
        Assert.True(ms.Length > 50);
        ms.Position = 0;
        Image back = Image.Load(ms);
        Assert.Equal(40, back.Width);
        Rgba32[] px = back.ToArray<Rgba32>();
        Assert.Equal(255, px[(20 * 40) + 5].R);
        Assert.Equal(0, px[(20 * 40) + 35].A);
    }

    [Fact]
    public void ResizeRendersAtTheFinalSizeInsteadOfScalingPixels()
    {
        // A one-pixel line at 100 px stays one pixel wide when the picture is drawn at 400 px (cosmetic pens are device-pixel wide).
        var emf = new EmfBuilder().CreatePen(1, 0, 1, Blue).Select(1).MoveTo(10, 50).LineTo(90, 50).Build();
        var (px, w, h) = Render(emf, width: 400, antialias: true);
        Assert.Equal(400, w);
        int rows = 0;
        for (int y = 0; y < h; y++)
        {
            if (px[(y * w) + 200].A > 200)
            {
                rows++;
            }
        }

        Assert.InRange(rows, 1, 2);
    }

    private static (int X0, int X1, int Y0, int Y1) Ink((Rgba32[] P, int W, int H) r)
    {
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (int y = 0; y < r.H; y++)
        {
            for (int x = 0; x < r.W; x++)
            {
                if (r.P[(y * r.W) + x].A > 100)
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        Assert.True(x1 >= 0, "something was drawn");
        return (x0, x1, y0, y1);
    }
}
