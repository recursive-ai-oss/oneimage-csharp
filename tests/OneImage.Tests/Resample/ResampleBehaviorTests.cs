using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.Resample;

public class ResampleBehaviorTests
{
    private static Image Solid(int w, int h, Rgba32 c)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, c);
        return Image.FromPixels<Rgba32>(px, w, h);
    }

    private static Image Random(int w, int h, int seed = 1)
    {
        var rnd = new Random(seed);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
        }

        return Image.FromPixels<Rgba32>(px, w, h);
    }

    // ---- filters ----

    [Theory]
    [InlineData(ResamplingFilter.Bilinear)]
    [InlineData(ResamplingFilter.CatmullRom)]
    [InlineData(ResamplingFilter.Mitchell)]
    [InlineData(ResamplingFilter.Lanczos2)]
    [InlineData(ResamplingFilter.Lanczos3)]
    [InlineData(ResamplingFilter.Box)]
    [InlineData(ResamplingFilter.Nearest)]
    public void WeightsSumToOneAndStayInRange(ResamplingFilter f)
    {
        foreach ((int n, int m) in new[] { (1, 5), (5, 1), (100, 37), (37, 100), (64, 64), (1000, 7), (3, 4) })
        {
            WeightTable t = WeightTable.Build(n, m, (double)n / m, f);
            for (int o = 0; o < m; o++)
            {
                double sum = 0;
                for (int i = 0; i < t.Count[o]; i++)
                {
                    sum += t.Weights[t.Offset[o] + i];
                }

                Assert.InRange(sum, 1 - 1e-6, 1 + 1e-6);
                Assert.InRange(t.Start[o], 0, n - 1);
                Assert.InRange(t.Start[o] + t.Count[o] - 1, 0, n - 1);
            }
        }
    }

    [Fact]
    public void KernelsMatchTheirDefinitions()
    {
        const double E = 1e-12;
        Assert.Equal(1, FilterFunctions.Evaluate(ResamplingFilter.Bilinear, 0), E);
        Assert.Equal(0.25, FilterFunctions.Evaluate(ResamplingFilter.Bilinear, -0.75), E);
        Assert.Equal(1, FilterFunctions.Evaluate(ResamplingFilter.CatmullRom, 0), E);
        Assert.Equal(0, FilterFunctions.Evaluate(ResamplingFilter.CatmullRom, 1), E);
        Assert.Equal(0.5625, FilterFunctions.Evaluate(ResamplingFilter.CatmullRom, 0.5), E);
        Assert.Equal(-0.0625, FilterFunctions.Evaluate(ResamplingFilter.CatmullRom, 1.5), E);
        Assert.Equal(8.0 / 9, FilterFunctions.Evaluate(ResamplingFilter.Mitchell, 0), E);
        Assert.Equal(1.0 / 18, FilterFunctions.Evaluate(ResamplingFilter.Mitchell, 1), E);
        Assert.Equal(0, FilterFunctions.Evaluate(ResamplingFilter.Mitchell, 2), E);
        Assert.Equal(1, FilterFunctions.Evaluate(ResamplingFilter.Lanczos3, 0), E);
        Assert.Equal(0, FilterFunctions.Evaluate(ResamplingFilter.Lanczos3, 1), 1e-12);
        Assert.Equal(3 * Math.Sin(Math.PI / 2) * Math.Sin(Math.PI / 6) / (Math.PI * Math.PI / 4), FilterFunctions.Evaluate(ResamplingFilter.Lanczos3, 0.5), E);
        Assert.Equal(0, FilterFunctions.Evaluate(ResamplingFilter.Lanczos3, 3), E);
        Assert.Equal(2 * Math.Sin(Math.PI * 0.5) * Math.Sin(Math.PI * 0.25) / (Math.PI * Math.PI * 0.25), FilterFunctions.Evaluate(ResamplingFilter.Lanczos2, 0.5), E);
        Assert.Equal(0, FilterFunctions.Evaluate(ResamplingFilter.Lanczos2, 2), E);
        // Reconstruction of a constant at any phase: the unnormalized cubic weights already partition unity.
        foreach (ResamplingFilter f in new[] { ResamplingFilter.CatmullRom, ResamplingFilter.Mitchell, ResamplingFilter.Bilinear })
        {
            for (double ph = 0; ph < 1; ph += 0.125)
            {
                double s = 0;
                for (int k = -3; k <= 4; k++)
                {
                    s += FilterFunctions.Evaluate(f, k - ph);
                }

                Assert.Equal(1, s, 1e-12);
            }
        }
    }

    [Fact]
    public void BoxWeightsAreExactAreaCoverage()
    {
        // 5 -> 2: output 0 covers [0, 2.5), output 1 covers [2.5, 5).
        WeightTable t = WeightTable.Build(5, 2, 2.5, ResamplingFilter.Box);
        Assert.Equal(0, t.Start[0]);
        Assert.Equal(3, t.Count[0]);
        Assert.Equal([0.4f, 0.4f, 0.2f], t.Weights.AsSpan(t.Offset[0], 3).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
        Assert.Equal(2, t.Start[1]);
        Assert.Equal([0.2f, 0.4f, 0.4f], t.Weights.AsSpan(t.Offset[1], 3).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
    }

    [Fact]
    public void AutoPicksLanczos3DownAndCatmullRomUp()
    {
        Assert.Equal(ResamplingFilter.Lanczos3, ResampleNode.Resolve(ResamplingFilter.Auto, 100, 50));
        Assert.Equal(ResamplingFilter.CatmullRom, ResampleNode.Resolve(ResamplingFilter.Auto, 50, 100));
        Assert.Equal(ResamplingFilter.Mitchell, ResampleNode.Resolve(ResamplingFilter.Mitchell, 50, 100));
        string g = Random(40, 40).Resize(20, 80, Fit.Stretch).DescribeGraph();
        Assert.Contains("filter_x=lanczos3", g, StringComparison.Ordinal);
        Assert.Contains("filter_y=catmull-rom", g, StringComparison.Ordinal);
    }

    // ---- exactness ----

    [Theory]
    [InlineData(ResamplingFilter.Auto)]
    [InlineData(ResamplingFilter.Lanczos3)]
    [InlineData(ResamplingFilter.Box)]
    [InlineData(ResamplingFilter.Nearest)]
    public void ConstantImageStaysConstant(ResamplingFilter f)
    {
        foreach (Rgba32 c in new[] { new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255), new Rgba32(17, 130, 243, 255), new Rgba32(200, 100, 50, 255) })
        {
            foreach ((int dw, int dh) in new[] { (7, 5), (300, 200), (1, 1), (90, 400) })
            {
                Rgba32[] got = Solid(120, 80, c).Resize(dw, dh, Fit.Stretch, filter: f).ToArray<Rgba32>();
                Assert.All(got, p => Assert.Equal(c, p));
            }
        }
    }

    [Fact]
    public void IdentityResizeIsExactAndOneAxisIdentityIsExactOnThatAxis()
    {
        Image img = Random(70, 50);
        Rgba32[] src = img.ToArray<Rgba32>();
        Image same = img.Resize(70, 50, Fit.Stretch);
        Assert.Same(img.Frames[0].Node, same.Frames[0].Node);
        Assert.Equal(src, same.ToArray<Rgba32>());

        // Width unchanged, height halved: every output column only mixes its own source column. Opaque, so the sRGB round trip is lossless.
        Image tall = Image.FromPixels<Rgba32>(Enumerable.Range(0, 70 * 50).Select(i => new Rgba32((byte)(i % 70 * 3), 9, 200, 255)).ToArray(), 70, 50);
        Rgba32[] r = tall.Resize(70, 25, Fit.Stretch, filter: ResamplingFilter.Box).ToArray<Rgba32>();
        for (int x = 0; x < 70; x++)
        {
            Assert.Equal((byte)(x * 3), r[x].R);
        }
    }

    [Fact]
    public void OpaqueRoundTripThroughLinearLightIsLossless()
    {
        // Same width, tiny height change on the other axis forces the pipeline; a 1-tap axis with weight 1 must not alter codes.
        var px = new Rgba32[256 * 2];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)(i % 256), (byte)(255 - (i % 256)), (byte)((i * 7) % 256), 255);
        }

        Rgba32[] got = Image.FromPixels<Rgba32>(px, 256, 2).Resize(256, 1, Fit.Stretch, filter: ResamplingFilter.Nearest).ToArray<Rgba32>();
        Assert.Equal(px.AsSpan(0, 256).ToArray(), got);
    }

    // ---- linear light and alpha ----

    private static Image Checker(int n)
    {
        var px = new Rgba32[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                byte v = ((x + y) & 1) == 0 ? (byte)255 : (byte)0;
                px[(y * n) + x] = new Rgba32(v, v, v, 255);
            }
        }

        return Image.FromPixels<Rgba32>(px, n, n);
    }

    [Theory]
    [InlineData(ResamplingFilter.Box)]
    [InlineData(ResamplingFilter.Bilinear)]
    public void CheckerboardAveragesInLinearLight(ResamplingFilter f)
    {
        Rgba32[] lin = Checker(16).Resize(8, 8, Fit.Stretch, filter: f).ToArray<Rgba32>();
        Rgba32[] enc = Checker(16).Resize(8, 8, Fit.Stretch, filter: f, linear: false).ToArray<Rgba32>();
        // Interior pixels: at the border, edge replication weights the outer pixel more.
        for (int y = 1; y < 7; y++)
        {
            for (int x = 1; x < 7; x++)
            {
                Assert.InRange(lin[(y * 8) + x].R, 187, 189);
                Assert.InRange(enc[(y * 8) + x].R, 127, 128);
            }
        }
    }

    [Fact]
    public void DefaultFilterOnCheckerboardIsNearTheLinearMean()
    {
        Rgba32[] lin = Checker(64).Resize(32, 32, Fit.Stretch).ToArray<Rgba32>();
        // Interior pixels only: the Lanczos kernel rings a little on a Nyquist pattern.
        for (int y = 4; y < 28; y++)
        {
            for (int x = 4; x < 28; x++)
            {
                Assert.InRange(lin[(y * 32) + x].R, 170, 205);
            }
        }
    }

    [Fact]
    public void PremultipliedAlphaAvoidsColorFringes()
    {
        // Left half opaque red; right half fully transparent but with blue color data hiding under it.
        var px = new Rgba32[64 * 8];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                px[(y * 64) + x] = x < 32 ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 255, 0);
            }
        }

        foreach (bool linear in new[] { true, false })
        {
            foreach (ResamplingFilter f in new[] { ResamplingFilter.Lanczos3, ResamplingFilter.Bilinear, ResamplingFilter.Mitchell })
            {
                Rgba32[] got = Image.FromPixels<Rgba32>(px, 64, 8).Resize(13, 3, Fit.Stretch, filter: f, linear: linear).ToArray<Rgba32>();
                bool sawEdge = false;
                foreach (Rgba32 p in got)
                {
                    if (p.A > 0)
                    {
                        Assert.True(p.B <= 1 && p.G <= 1 && p.R >= 254, $"fringe {p} ({f}, linear={linear})");
                    }

                    sawEdge |= p.A is > 0 and < 255;
                }

                Assert.True(sawEdge);
            }
        }
    }

    // ---- sizes ----

    [Theory]
    [InlineData(400, 300, 200, 200, Fit.Contain, 200, 150)]
    [InlineData(400, 300, 200, 200, Fit.Cover, 200, 200)]
    [InlineData(400, 300, 200, 200, Fit.Stretch, 200, 200)]
    [InlineData(400, 300, 200, 200, Fit.Pad, 200, 200)]
    [InlineData(400, 300, 100, null, Fit.Cover, 100, 75)]
    [InlineData(400, 300, null, 90, Fit.Pad, 120, 90)]
    [InlineData(400, 300, 1000, 1000, Fit.Contain, 1000, 750)]
    [InlineData(1, 1, 100, null, Fit.Contain, 100, 100)]
    [InlineData(1, 1, 7, 3, Fit.Contain, 3, 3)]
    [InlineData(1, 1, 7, 3, Fit.Cover, 7, 3)]
    [InlineData(1000, 10, 10, 10, Fit.Contain, 10, 1)]
    [InlineData(1000, 10, 10, 10, Fit.Cover, 10, 10)]
    [InlineData(10, 1000, 100, 100, Fit.Contain, 1, 100)]
    [InlineData(10, 1000, 100, 100, Fit.Pad, 100, 100)]
    [InlineData(1000, 10, 1, null, Fit.Contain, 1, 1)]
    [InlineData(333, 111, 100, null, Fit.Contain, 100, 33)]
    [InlineData(333, 111, null, 50, Fit.Contain, 150, 50)]
    [InlineData(100, 100, 200, 50, Fit.Contain, 50, 50)]
    public void FitSizes(int sw, int sh, int? w, int? h, Fit fit, int ew, int eh)
    {
        Image img = Image.FromNode(new RawBytesNode(new byte[sw * sh * 4], sw, sh, PixelFormat.Rgba8));
        Image r = img.Resize(w, h, fit);
        Assert.Equal((ew, eh), (r.Width, r.Height));
    }

    [Theory]
    [InlineData(100, 100, 200, 200, Fit.Contain, 100, 100)]
    [InlineData(100, 100, 200, 50, Fit.Cover, 100, 50)]
    [InlineData(100, 100, 200, 50, Fit.Stretch, 100, 50)]
    [InlineData(100, 100, 200, 50, Fit.Pad, 200, 50)]
    [InlineData(100, 100, 50, 50, Fit.Contain, 50, 50)]
    [InlineData(100, 100, 300, null, Fit.Contain, 100, 100)]
    [InlineData(100, 50, 400, 100, Fit.Cover, 100, 50)]
    public void UpscaleFalseNeverEnlarges(int sw, int sh, int? w, int? h, Fit fit, int ew, int eh)
    {
        Image img = Image.FromNode(new RawBytesNode(new byte[sw * sh * 4], sw, sh, PixelFormat.Rgba8));
        Image r = img.Resize(w, h, fit, upscale: false);
        Assert.Equal((ew, eh), (r.Width, r.Height));
        if (fit != Fit.Pad)
        {
            Assert.True(r.Width <= sw && r.Height <= sh);
        }
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        Image img = Solid(10, 10, default);
        Assert.Throws<ArgumentException>(() => img.Resize());
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(5, 5, (Fit)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(5, 5, filter: (ResamplingFilter)99));
    }

    // ---- anchors, pad, smart ----

    private static Image Columns(int w, int h) =>
        Image.FromPixels<Gray8>(Enumerable.Range(0, w * h).Select(i => new Gray8((byte)(i % w))).ToArray(), w, h);

    [Theory]
    [InlineData(Anchor.Left, 0)]
    [InlineData(Anchor.TopLeft, 0)]
    [InlineData(Anchor.BottomLeft, 0)]
    [InlineData(Anchor.Center, 50)]
    [InlineData(Anchor.Top, 50)]
    [InlineData(Anchor.Bottom, 50)]
    [InlineData(Anchor.Right, 100)]
    [InlineData(Anchor.TopRight, 100)]
    [InlineData(Anchor.BottomRight, 100)]
    public void CoverCropsAtTheAnchor(Anchor anchor, int firstColumn)
    {
        Image r = Columns(200, 100).Resize(100, 100, Fit.Cover, anchor);
        Gray8[] px = r.ToArray<Gray8>();
        Assert.Equal((100, 100), (r.Width, r.Height));
        Assert.Equal(firstColumn, px[0].V);
        Assert.Equal(firstColumn + 99, px[99].V);
    }

    [Fact]
    public void CoverCropsVerticalAnchors()
    {
        Image rows = Image.FromPixels<Gray8>(Enumerable.Range(0, 50 * 200).Select(i => new Gray8((byte)(i / 50))).ToArray(), 50, 200);
        Assert.Equal(0, rows.Resize(50, 50, Fit.Cover, Anchor.Top).ToArray<Gray8>()[0].V);
        Assert.Equal(75, rows.Resize(50, 50, Fit.Cover, Anchor.Center).ToArray<Gray8>()[0].V);
        Assert.Equal(150, rows.Resize(50, 50, Fit.Cover, Anchor.BottomRight).ToArray<Gray8>()[0].V);
    }

    [Theory]
    [InlineData(Anchor.TopLeft, 0)]
    [InlineData(Anchor.Center, 60)]
    [InlineData(Anchor.BottomRight, 120)]
    [InlineData(Anchor.Left, 0)]
    public void PadPlacesTheImageAtTheAnchor(Anchor anchor, int x)
    {
        // 60x120 content into a 180x120 box.
        Image img = Solid(60, 120, new Rgba32(200, 10, 10, 255));
        Rgba32 bg = new(0, 0, 255, 255);
        Image r = img.Resize(180, 120, Fit.Pad, anchor, background: bg);
        Assert.Equal((180, 120), (r.Width, r.Height));
        Rgba32[] px = r.ToArray<Rgba32>();
        for (int i = 0; i < px.Length; i++)
        {
            bool inside = i % 180 >= x && i % 180 < x + 60;
            Assert.Equal(inside ? new Rgba32(200, 10, 10, 255) : bg, px[i]);
        }

    }

    [Fact]
    public void PadDefaultsToTransparent()
    {
        Rgba32[] px = Solid(10, 10, new Rgba32(1, 2, 3, 255)).Resize(30, 10, Fit.Pad).ToArray<Rgba32>();
        Assert.Equal(default, px[0]);
        Assert.Equal(new Rgba32(1, 2, 3, 255), px[15]);
    }

    [Fact]
    public void SmartPicksTheDetailedRegion()
    {
        const int W = 200;
        const int H = 100;
        var px = new Gray8[W * H];
        Array.Fill(px, new Gray8(128));
        int patchPixels = 0;
        for (int y = 20; y < 80; y++)
        {
            for (int x = 140; x < 180; x++)
            {
                px[(y * W) + x] = new Gray8((byte)(((x / 3) + (y / 3)) % 2 == 0 ? 0 : 255));
                patchPixels++;
            }
        }

        Image r = Image.FromPixels<Gray8>(px, W, H).Resize(100, 100, Fit.Cover, Anchor.Smart);
        Assert.Equal((100, 100), (r.Width, r.Height));
        int detail = r.ToArray<Gray8>().Count(p => p.V != 128);
        Assert.Equal(patchPixels, detail);

        // The same on the vertical axis.
        var tall = new Gray8[H * W];
        Array.Fill(tall, new Gray8(128));
        for (int y = 140; y < 180; y++)
        {
            for (int x = 20; x < 80; x++)
            {
                tall[(y * H) + x] = new Gray8((byte)(((x / 3) + (y / 3)) % 2 == 0 ? 0 : 255));
            }
        }

        Image t = Image.FromPixels<Gray8>(tall, H, W).Resize(100, 100, Fit.Cover, Anchor.Smart);
        Assert.Equal(patchPixels, t.ToArray<Gray8>().Count(p => p.V != 128));
    }

    [Fact]
    public void SmartOnFlatImageFallsBackToCenter()
    {
        Image r = Columns(200, 10).Resize(100, 10, Fit.Cover, Anchor.Smart);
        Assert.Equal(50, r.ToArray<Gray8>()[0].V);
    }

    // ---- determinism, tiles, locality ----

    [Fact]
    public void ResultIsIndependentOfThreadsAndTileOrder()
    {
        Image img = Random(900, 700, 4).Resize(333, 250, Fit.Stretch);
        Rgba32[] a = img.ToArray<Rgba32>(maxDegree: 1);
        Rgba32[] b = img.ToArray<Rgba32>(maxDegree: 8);
        Assert.Equal(a, b);

        // Pull the tiles in reverse order, each on its own, and compare.
        Image fresh = Random(900, 700, 4).Resize(333, 250, Fit.Stretch);
        var piece = new Rgba32[128 * 128];
        for (int ty = 1; ty >= 0; ty--)
        {
            for (int tx = 2; tx >= 0; tx--)
            {
                var rect = new ImageRect(tx * 128, ty * 128, Math.Min(128, 333 - (tx * 128)), Math.Min(128, 250 - (ty * 128)));
                fresh.CopyPixelsTo<Rgba32>(piece, rect, maxDegree: 1);
                for (int y = 0; y < rect.Height; y++)
                {
                    for (int x = 0; x < rect.Width; x++)
                    {
                        Assert.Equal(a[((rect.Y + y) * 333) + rect.X + x], piece[(y * rect.Width) + x]);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(1000, 1000, 300, 300)]
    [InlineData(3000, 2000, 200, 133)]
    [InlineData(500, 500, 1100, 900)]
    public void OutputTileReadsOnlyItsInputRegionPlusMargin(int sw, int sh, int dw, int dh)
    {
        var src = new RawBytesNode(new byte[sw * sh * 4], sw, sh, PixelFormat.Rgba8, cacheable: false);
        Image img = Image.FromNode(src).Resize(dw, dh, Fit.Stretch);
        Node node = img.Frames[0].Node;
        ImageRect rect = node.TileRect(Math.Min(1, node.TilesX - 1), 0);
        ImageRect need = node.RequiredInput(0, rect);

        double ratioX = (double)sw / dw;
        double ratioY = (double)sh / dh;
        // 128 output pixels reach 128 * ratio source pixels, plus kernel margin on each side and box-block rounding.
        Assert.InRange(need.Width, 1, (int)((rect.Width * ratioX) + (2 * 3 * Math.Max(ratioX, 1) * 1.01) + 8 + (2 * ResampleNode.ReduceFactor(sw, dw))));
        Assert.InRange(need.Height, 1, (int)((rect.Height * ratioY) + (2 * 3 * Math.Max(ratioY, 1) * 1.01) + 8 + (2 * ResampleNode.ReduceFactor(sh, dh))));
        Assert.True(need.Width < sw && need.Height < sh);

        img.CopyPixelsTo<Rgba32>(new Rgba32[rect.Width * rect.Height], rect, maxDegree: 1);
        int tilesX = ((need.Right - 1) >> 7) - (need.X >> 7) + 1;
        int tilesY = ((need.Bottom - 1) >> 7) - (need.Y >> 7) + 1;
        Assert.Equal(tilesX * tilesY, src.Computed);
        Assert.True(src.Computed < src.TilesX * src.TilesY);
    }

    [Fact]
    public void CropAfterResizeComputesOnlyWhatItNeeds()
    {
        var src = new RawBytesNode(new byte[4000 * 3000 * 4], 4000, 3000, PixelFormat.Rgba8, cacheable: false);
        Image img = Image.FromNode(src).Resize(1000, 750, Fit.Stretch).Crop(0, 0, 100, 100);
        img.CopyPixelsTo<Rgba32>(new Rgba32[100 * 100], new ImageRect(0, 0, 100, 100), maxDegree: 1);
        Assert.True(src.Computed <= 25, $"computed {src.Computed} of {src.TilesX * src.TilesY}");
    }

    // ---- shrink on load ----

    [Theory]
    [InlineData(4000, 3000, 400, 8)]
    [InlineData(4000, 3000, 600, 4)]
    [InlineData(4000, 3000, 1500, 2)]
    [InlineData(4000, 3000, 3000, 1)]
    [InlineData(4000, 3000, 500, 8)]
    [InlineData(4000, 3000, 501, 4)]
    public void ShrinkOnLoadPicksTheLargestSafeDenominator(int w, int h, int target, int expected)
    {
        var fmt = new GradientFormat(DecoderCapabilities.Scale);
        Image img = Image.Load(GradientFormat.File(w, h), new LoadOptions { Configuration = fmt.Configuration() });
        Image r = img.Resize(width: target);
        Rgba32[] got = r.ToArray<Rgba32>();
        Assert.Equal(expected, fmt.LastScale);

        // Same resize on a decoder that cannot scale is the reference.
        var plain = new GradientFormat(DecoderCapabilities.None);
        Image refImg = Image.Load(GradientFormat.File(w, h), new LoadOptions { Configuration = plain.Configuration() }).Resize(width: target);
        Rgba32[] want = refImg.ToArray<Rgba32>();
        Assert.Equal(1, plain.LastScale);
        Assert.Equal((refImg.Width, refImg.Height), (r.Width, r.Height));
        int worst = 0;
        for (int i = 0; i < got.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(got[i].R - want[i].R), Math.Abs(got[i].G - want[i].G)));
        }

        Assert.True(worst <= 3, $"max difference {worst}");
    }

    [Fact]
    public void ShrinkOnLoadIsSkippedForNearestAndForUpscales()
    {
        var fmt = new GradientFormat(DecoderCapabilities.Scale);
        Image img = Image.Load(GradientFormat.File(2000, 1000), new LoadOptions { Configuration = fmt.Configuration() });
        _ = img.Resize(width: 100, filter: ResamplingFilter.Nearest).ToArray<Rgba32>();
        Assert.Equal(1, fmt.LastScale);
        _ = img.Resize(width: 4000).Crop(0, 0, 10, 10).ToArray<Rgba32>();
        Assert.Equal(1, fmt.LastScale);
    }

    // ---- kernels ----

    [Fact]
    public void ReportHardwarePath() =>
        TestContext.Current.SendDiagnosticMessage($"hardware: Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated} Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Avx512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(130)]
    public void VerticalFilterMatchesScalarBitForBit(int len)
    {
        var rnd = new Random(len);
        int stride = len + 3;
        float[] rows = Enumerable.Range(0, stride * 9).Select(_ => rnd.NextSingle() * 3).ToArray();
        float[] w = Enumerable.Range(0, 5).Select(_ => rnd.NextSingle() - 0.2f).ToArray();
        var got = new float[len];
        ResampleKernel.VerticalFilter(rows, stride, 2, w, got);
        for (int j = 0; j < len; j++)
        {
            float acc = rows[(2 * stride) + j] * w[0];
            for (int t = 1; t < 5; t++)
            {
                acc += rows[((2 + t) * stride) + j] * w[t];
            }

            Assert.Equal(BitConverter.SingleToInt32Bits(acc), BitConverter.SingleToInt32Bits(got[j]));
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(7, 4)]
    [InlineData(33, 4)]
    [InlineData(17, 1)]
    [InlineData(9, 2)]
    public void HorizontalAndReduceMatchScalarBitForBit(int outPixels, int stride)
    {
        var rnd = new Random(outPixels);
        int srcPixels = (outPixels * 2) + 5;
        float[] src = Enumerable.Range(0, srcPixels * stride).Select(_ => rnd.NextSingle()).ToArray();
        WeightTable t = WeightTable.Build(srcPixels, outPixels, (double)srcPixels / outPixels, ResamplingFilter.Lanczos3);
        var got = new float[outPixels * stride];
        ResampleKernel.HorizontalFilter(src, 0, got, stride, t.Start, t.Count, t.Offset, t.Weights);
        for (int i = 0; i < outPixels; i++)
        {
            for (int k = 0; k < stride; k++)
            {
                float acc = src[(t.Start[i] * stride) + k] * t.Weights[t.Offset[i]];
                for (int j = 1; j < t.Count[i]; j++)
                {
                    acc += src[((t.Start[i] + j) * stride) + k] * t.Weights[t.Offset[i] + j];
                }

                Assert.Equal(BitConverter.SingleToInt32Bits(acc), BitConverter.SingleToInt32Bits(got[(i * stride) + k]));
            }
        }

        int red = (srcPixels + 2) / 3;
        var r = new float[red * stride];
        ResampleKernel.ReduceRow(src, r, stride, 3, srcPixels, red);
        for (int i = 0; i < red; i++)
        {
            int n = Math.Min(3, srcPixels - (i * 3));
            for (int k = 0; k < stride; k++)
            {
                float acc = src[(i * 3 * stride) + k];
                for (int j = 1; j < n; j++)
                {
                    acc += src[(((i * 3) + j) * stride) + k];
                }

                Assert.Equal(BitConverter.SingleToInt32Bits(acc * (1f / n)), BitConverter.SingleToInt32Bits(r[(i * stride) + k]));
            }
        }
    }

    [Theory]
    [InlineData(1, 2, true, true)]
    [InlineData(2, 2, true, true)]
    [InlineData(5, 3, false, true)]
    [InlineData(31, 7, true, true)]
    [InlineData(100, 7, true, false)]
    [InlineData(101, 4, false, false)]
    [InlineData(257, 15, true, true)]
    [InlineData(300, 1, true, false)]
    public void FusedUnpackAndReduceMatchesTheTwoStepsBitForBit(int width, int factor, bool linear, bool alpha)
    {
        var layout = new ResampleLayout(alpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8, linear);
        Assert.True(ResampleKernel.CanFuseReduce(layout));
        int channels = alpha ? 4 : 3;
        var rnd = new Random(width * 31 + factor);
        byte[] src = new byte[width * channels];
        rnd.NextBytes(src);
        // Mix opaque runs with arbitrary alpha so both the shortcut and the multiply are taken.
        for (int x = 0; alpha && x < width; x++)
        {
            src[(x * 4) + 3] = (x / 5) % 2 == 0 ? (byte)255 : src[(x * 4) + 3];
        }

        int outPixels = (width + factor - 1) / factor;
        var floats = new float[width * 4];
        ResampleKernel.UnpackRow(src, floats, width, layout);
        var want = new float[outPixels * 4];
        ResampleKernel.ReduceRow(floats, want, 4, factor, width, outPixels);
        var got = new float[outPixels * 4];
        ResampleKernel.UnpackReduceRow(src, got, width, factor, outPixels, layout);
        for (int i = 0; i < want.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(want[i]), BitConverter.SingleToInt32Bits(got[i]));
        }
    }

    [Fact]
    public void KernelsRejectTablesThatReadOutsideTheBuffer()
    {
        WeightTable t = WeightTable.Build(100, 10, 10, ResamplingFilter.Box);
        Assert.Throws<ArgumentException>(() => ResampleKernel.HorizontalFilter(new float[40], 0, new float[40], 4, t.Start, t.Count, t.Offset, t.Weights));
        Assert.Throws<ArgumentException>(() => ResampleKernel.VerticalFilter(new float[10], 10, 1, new float[2], new float[10]));
    }

    [Fact]
    public void ExtremeRatiosDoNotBlowUp()
    {
        Image big = Image.FromNode(new RawBytesNode(new byte[20000 * 4], 20000, 1, PixelFormat.Rgba8));
        Rgba32[] r = big.Resize(1, 1, Fit.Stretch).ToArray<Rgba32>();
        Assert.Single(r);
        Image tiny = Solid(1, 1, new Rgba32(9, 8, 7, 255));
        Rgba32[] up = tiny.Resize(500, 500, Fit.Stretch).ToArray<Rgba32>();
        Assert.All(up, p => Assert.Equal(new Rgba32(9, 8, 7, 255), p));
    }
}
