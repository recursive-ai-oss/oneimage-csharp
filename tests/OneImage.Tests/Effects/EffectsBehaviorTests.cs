using Xunit;

namespace OneImage.Tests.Effects;

/// <summary>Behavior of the effect operations: examples, tiled rendering, streaming sources, formats and argument checks.</summary>
[Collection(MemoryMeasurement.Name)]
public class EffectsBehaviorTests
{
    private const int W = 300;
    private const int H = 270;

    private static readonly (string Name, Func<Image, Image> Op)[] s_ops =
    [
        ("pixelate 9", i => i.Pixelate(9)),
        ("pixelate 200", i => i.Pixelate(200)),
        ("oil 10/15", i => i.OilPaint(10, 15)),
        ("oil 4/4", i => i.OilPaint(4, 4)),
        ("vignette", i => i.Vignette()),
        ("vignette area", i => i.Vignette(new Rgba32(10, 20, 200, 180), 90, 60, new ImageRect(30, 40, 200, 150))),
        ("glow", i => i.Glow(new Rgba32(255, 220, 100, 255), 0)),
        ("glow area", i => i.Glow(new Rgba32(255, 220, 100, 200), 60, new ImageRect(100, 100, 150, 120))),
        ("bokeh 6/2", i => i.BokehBlur(6, 2, 3)),
        ("bokeh 9/4 mirror", i => i.BokehBlur(9, 4, 2, EdgeMode.Mirror)),
        ("gaussian sharpen 2", i => i.GaussianSharpen(2)),
        ("gaussian sharpen 2 linear", i => i.GaussianSharpen(2, linear: true)),
        ("edges kirsch", i => i.DetectEdges(EdgeDetector.Kirsch)),
        ("edges roberts color", i => i.DetectEdges(EdgeDetector.RobertsCross, grayscale: false)),
        ("edges laplacian 5x5 mirror", i => i.DetectEdges(EdgeDetector.Laplacian5x5, edge: EdgeMode.Mirror)),
        ("edges log constant", i => i.DetectEdges(EdgeDetector.LaplacianOfGaussian, grayscale: false, edge: EdgeMode.Constant)),
    ];

    private static Image Source(byte alpha = 255) => EffectsOracleTests.Ours(EffectsOracleTests.Picture(W, H, 21, alpha), W, H);

    [Fact]
    public void RegionRenderEqualsFullRenderCropped()
    {
        foreach ((string name, Func<Image, Image> op) in s_ops)
        {
            Image img = op(Source(200));
            Rgba32[] full = img.ToArray<Rgba32>();
            foreach (ImageRect r in new[] { new ImageRect(100, 90, 130, 120), new ImageRect(0, 0, 129, 129), new ImageRect(270, 250, 30, 20), new ImageRect(127, 127, 2, 2) })
            {
                Image fresh = op(Source(200));
                Rgba32[] part = new Rgba32[r.Width * r.Height];
                fresh.CopyPixelsTo<Rgba32>(part, r);
                for (int y = 0; y < r.Height; y++)
                {
                    for (int x = 0; x < r.Width; x++)
                    {
                        Assert.True(part[(y * r.Width) + x] == full[((r.Y + y) * W) + r.X + x], $"{name}: region {r} differs at ({x},{y})");
                    }
                }
            }
        }
    }

    [Fact]
    public void ResultDoesNotDependOnThreadCount()
    {
        foreach ((string name, Func<Image, Image> op) in s_ops)
        {
            Rgba32[] one = op(Source()).ToArray<Rgba32>(maxDegree: 1);
            Rgba32[] many = op(Source()).ToArray<Rgba32>(maxDegree: 8);
            Assert.True(one.AsSpan().SequenceEqual(many), $"{name}: result depends on the thread count");
        }
    }

    [Fact]
    public void OtherPixelFormatsAreSupported()
    {
        Image rgba = Source();
        foreach ((string name, Func<Image, Image> op) in s_ops)
        {
            foreach (PixelFormat f in new[] { PixelFormat.Gray8, PixelFormat.Rgb16, PixelFormat.RgbaF32, PixelFormat.GrayAlpha8, PixelFormat.Rgba8Premultiplied })
            {
                Image img = op(rgba.ConvertTo(f));
                Assert.Equal(W, img.Width);
                Assert.Equal(H, img.Height);
                _ = img.ToArray<Rgba32>();
            }
        }
    }

    [Fact]
    public void CmykIsRefusedWhereNotDefined()
    {
        Image cmyk = Source().ConvertTo(PixelFormat.Cmyk8);
        Assert.Throws<ImageOperationException>(() => cmyk.OilPaint());
        Assert.Throws<ImageOperationException>(() => cmyk.Vignette());
        Assert.Throws<ImageOperationException>(() => cmyk.Glow());
        Assert.Throws<ImageOperationException>(() => cmyk.DetectEdges());
        _ = cmyk.Pixelate(8).ToArray<Rgba32>();
    }

    [Fact]
    public void PixelateTakesTheBlockCenterAndKeepsAllFormats()
    {
        var px = new Rgba32[14 * 9];
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 14; x++)
            {
                px[(y * 14) + x] = new Rgba32((byte)(x * 10), (byte)(y * 10), 7, 255);
            }
        }

        Rgba32[] r = Image.FromPixels<Rgba32>(px, 14, 9).Pixelate(4).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(20, 20, 7, 255), r[0]);
        Assert.Equal(new Rgba32(20, 20, 7, 255), r[(3 * 14) + 3]);
        Assert.Equal(new Rgba32(60, 20, 7, 255), r[4]);
        Assert.Equal(new Rgba32(130, 60, 7, 255), r[(7 * 14) + 13]);
        // The last block row (y = 8) is partial: its center row is clamped to the last row.
        Assert.Equal(new Rgba32(130, 80, 7, 255), r[(8 * 14) + 13]);
        Image s = Source();
        Assert.Same(s, s.Pixelate(1));
    }

    [Fact]
    public void VignetteLeavesTheCenterAndDarkensTheCorners()
    {
        byte[] white = new byte[101 * 61 * 4];
        Array.Fill(white, (byte)255);
        Rgba32[] r = EffectsOracleTests.Ours(white, 101, 61).Vignette().ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 255, 255, 255), r[(30 * 101) + 50]);
        Rgba32 corner = r[0];
        Assert.True(corner.R < 60 && corner.R == corner.G && corner.G == corner.B && corner.A == 255, $"corner {corner}");
        // Darker the further from the center, along a row.
        for (int x = 51; x < 101; x++)
        {
            Assert.True(r[(30 * 101) + x].R <= r[(30 * 101) + x - 1].R);
        }

        // A transparent vignette color changes nothing.
        Assert.True(EffectsOracleTests.Ours(white, 101, 61).Vignette(new Rgba32(0, 0, 0, 0)).ToArray<Rgba32>().AsSpan().SequenceEqual(EffectsOracleTests.Ours(white, 101, 61).ToArray<Rgba32>()));
    }

    [Fact]
    public void GlowTintsTheCenterAndLeavesTheOutside()
    {
        byte[] white = new byte[101 * 101 * 4];
        Array.Fill(white, (byte)255);
        Rgba32[] r = EffectsOracleTests.Ours(white, 101, 101).Glow(new Rgba32(0, 0, 255, 255), 30).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 255, 255), r[(50 * 101) + 50]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), r[0]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), r[(50 * 101) + 90]);
    }

    [Fact]
    public void EdgeDetectorsRespondToAStepAndNotToFlatAreas()
    {
        var px = new Gray8[40 * 40];
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                px[(y * 40) + x] = new Gray8(x < 20 ? (byte)0 : (byte)40);
            }
        }

        Image step = Image.FromPixels<Gray8>(px, 40, 40);
        // (Kayyali's kernels are diagonal ones and cancel on a straight vertical step.)
        foreach (EdgeDetector d in Enum.GetValues<EdgeDetector>().Where(d => d != EdgeDetector.Kayyali))
        {
            Gray8[] e = step.DetectEdges(d).ToArray<Gray8>();
            Assert.Equal(0, e[(20 * 40) + 5].V);
            Assert.Equal(0, e[(20 * 40) + 35].V);
            Assert.True(e[(20 * 40) + 19].V + e[(20 * 40) + 20].V > 0, $"{d}: no response at the step");
        }

        // Sobel: gx = 4 * 40 at the two pixels next to the step, gy = 0.
        Gray8[] sobel = step.DetectEdges(EdgeDetector.Sobel).ToArray<Gray8>();
        Assert.Equal(160, sobel[(20 * 40) + 19].V);
        Assert.Equal(160, sobel[(20 * 40) + 20].V);
        // Prewitt: 3 * 40.
        Assert.Equal(120, step.DetectEdges(EdgeDetector.Prewitt).ToArray<Gray8>()[(20 * 40) + 20].V);
    }

    [Fact]
    public void ColorEdgesKeepFormatAndAlpha()
    {
        Image img = Source(77);
        Image e = img.DetectEdges(EdgeDetector.Scharr, grayscale: false);
        Assert.Equal(PixelFormat.Rgba8, e.Format);
        Assert.All(e.ToArray<Rgba32>(), p => Assert.Equal(77, p.A));
        Image g = img.DetectEdges(EdgeDetector.Scharr);
        Assert.Equal(PixelFormat.Gray8, g.Format);
    }

    [Fact]
    public void OilPaintFlattensNoiseAndKeepsAlpha()
    {
        var rnd = new Random(5);
        var px = new Rgba32[60 * 60];
        for (int i = 0; i < px.Length; i++)
        {
            int n = rnd.Next(-3, 4);
            px[i] = new Rgba32((byte)(100 + n), (byte)(150 + n), (byte)(200 + n), 91);
        }

        Rgba32[] r = Image.FromPixels<Rgba32>(px, 60, 60).OilPaint(10, 9).ToArray<Rgba32>();
        Assert.All(r, p => Assert.Equal(91, p.A));
        Assert.True(r.Distinct().Count() < 12, "noise should collapse to a handful of colors");
        // The window is centered: a 9x9 window around a dividing line puts the line half-way.
        var half = new Gray8[41 * 41];
        for (int y = 0; y < 41; y++)
        {
            for (int x = 0; x < 41; x++)
            {
                half[(y * 41) + x] = new Gray8(x < 20 ? (byte)0 : (byte)255);
            }
        }

        Gray8[] h = Image.FromPixels<Gray8>(half, 41, 41).OilPaint(2, 9).ToArray<Gray8>();
        Assert.Equal(0, h[(20 * 41) + 19].V);
        Assert.Equal(255, h[(20 * 41) + 21].V);
        Assert.Equal(0, h[(20 * 41) + 10].V);
        Assert.Equal(255, h[(20 * 41) + 30].V);
    }

    [Fact]
    public void BokehBlurSpreadsAPointIntoADiscAndKeepsFlatAreas()
    {
        var px = new Gray8[101 * 101];
        for (int y = 46; y <= 54; y++)
        {
            for (int x = 46; x <= 54; x++)
            {
                px[(y * 101) + x] = new Gray8(255);
            }
        }

        Gray8[] r = Image.FromPixels<Gray8>(px, 101, 101).BokehBlur(20, 2, 1).ToArray<Gray8>();
        // Weight sits inside the aperture and almost none outside it.
        long inside = 0;
        long outside = 0;
        for (int y = 0; y < 101; y++)
        {
            for (int x = 0; x < 101; x++)
            {
                double d = Math.Sqrt(((x - 50) * (x - 50)) + ((y - 50) * (y - 50)));
                if (d <= 20)
                {
                    inside += r[(y * 101) + x].V;
                }
                else
                {
                    outside += r[(y * 101) + x].V;
                }
            }
        }

        Assert.True(inside > 0 && outside * 10 < inside, $"inside {inside}, outside {outside}");
        byte[] flat = new byte[50 * 50 * 4];
        Array.Fill(flat, (byte)130);
        Assert.All(EffectsOracleTests.Ours(flat, 50, 50).BokehBlur(12).ToArray<Rgba32>(), p => Assert.Equal(new Rgba32(130, 130, 130, 130), p));
    }

    [Fact]
    public void BokehRadiusLargerThanTheImageIsStillCorrect()
    {
        Image small = EffectsOracleTests.Ours(EffectsOracleTests.Picture(40, 30, 8), 40, 30);
        Rgba32[] a = small.BokehBlur(100, 2, 1).ToArray<Rgba32>();
        Assert.Equal(1200, a.Length);
        Rgba32[] b = small.BokehBlur(100, 2, 1).ToArray<Rgba32>(maxDegree: 1);
        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void GaussianSharpenKeepsFlatAreasAndSigmaZeroIsIdentity()
    {
        byte[] flat = new byte[30 * 30 * 4];
        Array.Fill(flat, (byte)77);
        Assert.All(EffectsOracleTests.Ours(flat, 30, 30).GaussianSharpen(2).ToArray<Rgba32>(), p => Assert.Equal(new Rgba32(77, 77, 77, 77), p));
        Image img = Source();
        Assert.Same(img, img.GaussianSharpen(0));
    }

    [Fact]
    public void EntropyCropFindsTheTexturedRectangle()
    {
        var px = new Rgba32[200 * 120];
        Array.Fill(px, new Rgba32(90, 90, 90, 255));
        var rnd = new Random(3);
        for (int y = 30; y < 90; y++)
        {
            for (int x = 40; x < 150; x++)
            {
                byte v = (byte)rnd.Next(256);
                px[(y * 200) + x] = new Rgba32(v, (byte)(255 - v), v, 255);
            }
        }

        Image img = Image.FromPixels<Rgba32>(px, 200, 120);
        Image c = img.EntropyCrop();
        // The detector's own window is one pixel; the crop is the textured area, within a pixel of it on each side.
        Assert.InRange(c.Width, 108, 112);
        Assert.InRange(c.Height, 58, 62);
        Image flatImage = Image.FromPixels<Rgba32>(new Rgba32[50 * 50], 50, 50);
        Assert.Same(flatImage, flatImage.EntropyCrop());
        Assert.Throws<ArgumentOutOfRangeException>(() => img.EntropyCrop(1.5f));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image img = Source();
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Pixelate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.OilPaint(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.OilPaint(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.OilPaint(10, 300));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Vignette(radiusX: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Vignette(area: new ImageRect(0, 0, W + 1, H)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Glow(radius: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BokehBlur(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BokehBlur(300));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BokehBlur(5, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BokehBlur(5, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BokehBlur(5, 2, 3, EdgeMode.Wrap));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GaussianSharpen(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GaussianSharpen(50));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.DetectEdges((EdgeDetector)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.DetectEdges(edge: EdgeMode.Wrap));
    }

    [Fact]
    public void DescriptorsHaveLanguageNeutralNamesAndParameters()
    {
        Assert.Contains("pixelate", Source().Pixelate(5).DescribeGraph());
        Assert.Contains("oil_paint", Source().OilPaint().DescribeGraph());
        Assert.Contains("vignette", Source().Vignette().DescribeGraph());
        Assert.Contains("glow", Source().Glow().DescribeGraph());
        Assert.Contains("bokeh_blur", Source().BokehBlur(4).DescribeGraph());
        Assert.Contains("gaussian_sharpen", Source().GaussianSharpen().DescribeGraph());
        string edges = Source().DetectEdges(EdgeDetector.Kirsch, false).DescribeGraph();
        Assert.Contains("detect_edges", edges);
        Assert.Contains("Kirsch", edges);
    }

    [Fact]
    public void AnimatedImagesAreProcessedFrameByFrame()
    {
        Image still = Source();
        Image inverted = still.Invert();
        var anim = new Image([still.Frames[0].Node, inverted.Frames[0].Node], [TimeSpan.Zero, TimeSpan.Zero], 0, ImageMetadata.Empty, null);
        Image r = anim.Pixelate(8);
        Assert.Equal(2, r.FrameCount);
        Assert.True(r.Frames[1].ToArray<Rgba32>().AsSpan().SequenceEqual(inverted.Pixelate(8).ToArray<Rgba32>()));
    }

    // ---- locality and streaming ----

    private static void CheckOneTile(Func<Image, Image> op, int tx, int ty, int expectedTiles)
    {
        var src = new Resample.RawBytesNode(new byte[1000 * 1000 * 4], 1000, 1000, PixelFormat.Rgba8, cacheable: false);
        Image img = op(Image.FromNode(src));
        Node node = img.Frames[0].Node;
        ImageRect tile = node.TileRect(tx, ty);
        ImageRect need = node.RequiredInput(0, tile);
        img.CopyPixelsTo<Rgba32>(new Rgba32[tile.Width * tile.Height], tile, maxDegree: 1);
        Assert.Equal(expectedTiles, src.Computed);
        int tiles = (((need.Right - 1) >> 7) - (need.X >> 7) + 1) * (((need.Bottom - 1) >> 7) - (need.Y >> 7) + 1);
        Assert.Equal(tiles, src.Computed);
    }

    [Fact]
    public void TilesReadOnlyTheirRegionPlusTheMargin()
    {
        CheckOneTile(i => i.DetectEdges(EdgeDetector.Sobel), 3, 3, 9);
        CheckOneTile(i => i.DetectEdges(EdgeDetector.Laplacian5x5, false), 3, 3, 9);
        CheckOneTile(i => i.DetectEdges(EdgeDetector.Kirsch), 0, 0, 4);
        CheckOneTile(i => i.GaussianSharpen(2), 3, 3, 9);
        CheckOneTile(i => i.BokehBlur(10), 3, 3, 9);
        CheckOneTile(i => i.OilPaint(10, 9), 3, 3, 9);
        CheckOneTile(i => i.OilPaint(10, 9), 7, 7, 4);
        // The blocks at a tile's left and top border started in the tile before it, so their centers can lie there.
        CheckOneTile(i => i.Pixelate(9), 3, 3, 4);
        CheckOneTile(i => i.Pixelate(200), 3, 3, 4);
        CheckOneTile(i => i.Pixelate(900), 3, 3, 1);
        // No margin at all.
        CheckOneTile(i => i.Vignette(), 3, 3, 1);
        CheckOneTile(i => i.Glow(), 3, 3, 1);
    }

    [Fact]
    public void VignetteAreaLeavesOtherTilesUntouched()
    {
        var src = new Resample.RawBytesNode(new byte[1000 * 1000 * 4], 1000, 1000, PixelFormat.Rgba8, cacheable: false);
        Image img = Image.FromNode(src).Vignette(area: new ImageRect(0, 0, 200, 200));
        Node node = img.Frames[0].Node;
        // A tile wholly outside the area is the input tile itself.
        img.CopyPixelsTo<Rgba32>(new Rgba32[128 * 128], node.TileRect(6, 6), maxDegree: 1);
        Assert.Equal(1, src.Computed);
    }

    private static Image LoadGen(int w, int h) =>
        Image.Load(GenFormat.Header(w, h), new LoadOptions { Configuration = GenFormat.Instance.Configuration() });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void StreamedResultEqualsMaterializedResult(int which)
    {
        const int Wd = 1500;
        const int Ht = 5000;
        Func<Image, Image> op = which switch
        {
            0 => i => i.DetectEdges(EdgeDetector.Kirsch, grayscale: false),
            1 => i => i.Pixelate(11),
            2 => i => i.OilPaint(8, 7),
            3 => i => i.Vignette(),
            4 => i => i.GaussianSharpen(2),
            5 => i => i.BokehBlur(5, 2, 3),
            _ => i => i.DetectEdges(EdgeDetector.Laplacian5x5),
        };
        Image streamed = LoadGen(Wd, Ht);
        Assert.True(streamed.Frames[0].Node.Streaming);
        Image materialized = Image.FromPixels<Rgba32>(streamed.ToArray<Rgba32>(), Wd, Ht);
        Image viaStream = op(LoadGen(Wd, Ht));
        Image reference = op(materialized);
        Assert.Equal(reference.Format, viaStream.Format);
        Assert.True(reference.ToArray<Rgba32>().AsSpan().SequenceEqual(viaStream.ToArray<Rgba32>()), "streamed and materialized results differ");
    }

    [Fact]
    public void SmallMarginEffectsOnHugeSequentialSourceStayInBoundedMemory()
    {
        const int Wd = 8000;
        const int Ht = 20000;
        long frameBytes = (long)Wd * Ht * 4;
        foreach ((string name, Func<Image, Image> op) in new (string, Func<Image, Image>)[]
        {
            ("pixelate 8", i => i.Pixelate(8)),
            ("vignette", i => i.Vignette()),
            ("edges sobel", i => i.DetectEdges(EdgeDetector.Sobel)),
            ("gaussian sharpen 2", i => i.GaussianSharpen(2)),
        })
        {
            Image img = op(LoadGen(Wd, Ht));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(true);
            long peak = baseline;
            using var stop = new CancellationTokenSource();
            var sampler = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    peak = Math.Max(peak, LiveHeap.Read(baseline, frameBytes / 10));
                    Thread.Sleep(2);
                }
            });
            sampler.Start();
            try
            {
                Rgba32[] strip = new Rgba32[img.Width * Tile.Size];
                for (int y = 0; y < img.Height; y += Tile.Size)
                {
                    img.Frames[0].CopyPixelsTo<Rgba32>(strip, new ImageRect(0, y, img.Width, Math.Min(Tile.Size, img.Height - y)));
                }
            }
            finally
            {
                stop.Cancel();
                sampler.Join();
            }

            long growth = peak - baseline;
            Assert.True(growth < frameBytes / 10, $"{name}: peak growth {growth} exceeds 10% of the frame ({frameBytes})");
        }
    }
}
