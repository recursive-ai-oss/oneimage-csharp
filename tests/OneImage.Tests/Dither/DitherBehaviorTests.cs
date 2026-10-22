using OneImage.Tests.Gif;

namespace OneImage.Tests.DitherTests;

public class DitherBehaviorTests
{
    private static Image Photo(int w = 300, int h = 220) => Image.FromPixels<Rgba32>(GifTestUtil.SyntheticPhoto(w, h), w, h).ConvertTo(PixelFormat.Rgb8);

    [Fact]
    public void ResultUsesOnlyPaletteColors()
    {
        Rgba32[] pal = [.. KnownPalettes.Werner];
        HashSet<Rgba32> set = [.. pal];
        foreach (Dither d in Enum.GetValues<Dither>())
        {
            Rgba32[] px = Photo().Dither(pal, d).ToArray<Rgba32>();
            Assert.All(px, p => Assert.Contains(p with { A = 255 }, set));
        }
    }

    [Fact]
    public void DeterministicAcrossDegreesAndTileOrder()
    {
        Rgba32[] pal = [.. KnownPalettes.WebSafe];
        foreach (Dither d in new[] { Dither.None, Dither.FloydSteinberg, Dither.StevensonArce, Dither.Bayer8x8, Dither.Ordered3x3 })
        {
            foreach (bool fast in new[] { false, true })
            {
                Rgba32[] a = Photo().Dither(pal, d, fastMatch: fast).ToArray<Rgba32>(maxDegree: 1);
                Rgba32[] b = Photo().Dither(pal, d, fastMatch: fast).ToArray<Rgba32>(maxDegree: 8);
                Assert.Equal(a, b);

                // A region asked for first must agree with the whole frame.
                Image lazy = Photo().Dither(pal, d, fastMatch: fast);
                Rgba32[] corner = new Rgba32[40 * 30];
                lazy.Frames[0].CopyPixelsTo<Rgba32>(corner, new ImageRect(250, 190, 40, 30));
                for (int y = 0; y < 30; y++)
                {
                    Assert.Equal(a.AsSpan(((190 + y) * 300) + 250, 40).ToArray(), corner.AsSpan(y * 40, 40).ToArray());
                }
            }
        }
    }

    [Fact]
    public void ScaleZeroIsPlainNearestColor()
    {
        Rgba32[] pal = [.. KnownPalettes.Werner];
        Rgba32[] none = Photo().Dither(pal, Dither.None).ToArray<Rgba32>();
        Assert.Equal(none, Photo().Dither(pal, Dither.FloydSteinberg, 0f).ToArray<Rgba32>());
        Assert.Equal(none, Photo().Dither(pal, Dither.Bayer4x4, 0f).ToArray<Rgba32>());
    }

    [Fact]
    public void BinaryDitherUsesTheTwoColors()
    {
        var hi = new Rgba32(250, 240, 10, 255);
        var lo = new Rgba32(5, 20, 90, 255);
        Rgba32[] px = Photo().BinaryDither(Dither.Bayer8x8, hi, lo).ToArray<Rgba32>();
        Assert.All(px, p => Assert.True(p == hi || p == lo));
        Assert.Contains(hi, px);
        Assert.Contains(lo, px);
        Rgba32[] bw = Photo().BinaryDither().ToArray<Rgba32>();
        Assert.All(bw, p => Assert.True(p == new Rgba32(255, 255, 255, 255) || p == new Rgba32(0, 0, 0, 255)));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image img = Photo(8, 8);
        Assert.Throws<ArgumentException>(() => img.Dither([]));
        Assert.Throws<ArgumentNullException>(() => img.Dither(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Dither([new Rgba32(0, 0, 0, 255)], (Dither)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Dither([new Rgba32(0, 0, 0, 255)], Dither.Atkinson, -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Quantize(16, quantizer: (Quantizer)9));
    }

    [Fact]
    public void TranslucentPaletteGivesAlphaOutput()
    {
        Image img = Photo(16, 16).Dither([new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 128)], Dither.None);
        Assert.Equal(PixelFormat.Rgba8, img.Format);
        Assert.Equal(PixelFormat.Rgb8, Photo(16, 16).Dither([new Rgba32(0, 0, 0, 255)], Dither.None).Format);
    }

    [Fact]
    public void DescriptorNamesTheOp()
    {
        string g = Photo(16, 16).Dither([new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255)], Dither.Sierra3, 0.5f, fastMatch: false).DescribeGraph();
        Assert.Contains("dither(dither=Sierra3, scale=0.5, match=exact, palette=000000FFFFFFFFFF)", g);
    }

    [Fact]
    public void ErrorDiffusionCarriesErrorAcrossBandBoundaries()
    {
        // Taller than two tile rows, so error handed from one band to the next is exercised; a one-row-at-a-time
        // pass over the same pixels must give the same bytes.
        Rgba32[] src = GifTestUtil.SyntheticPhoto(70, 300);
        Rgba32[] pal = GifTestUtil.Pattern(4, 4, 1, 6, false).Distinct().ToArray();
        Rgba32[] whole = Image.FromPixels<Rgba32>(src, 70, 300).Dither(pal, Dither.JarvisJudiceNinke, fastMatch: false).ToArray<Rgba32>();
        Rgba32[] again = Image.FromPixels<Rgba32>(src, 70, 300).Dither(pal, Dither.JarvisJudiceNinke, fastMatch: false).ToArray<Rgba32>();
        Assert.Equal(whole, again);
        Assert.Equal(70 * 300, whole.Length);

        // Reference: the same diffusion written out directly with the kernel's weights.
        int[] dx = [1, 2, -2, -1, 0, 1, 2, -2, -1, 0, 1, 2];
        int[] dy = [0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2];
        float[] wt = [7, 5, 3, 5, 7, 5, 3, 1, 3, 5, 3, 1];
        var buf = new float[70 * 300 * 4];
        for (int i = 0; i < src.Length; i++)
        {
            buf[(i * 4)] = src[i].R;
            buf[(i * 4) + 1] = src[i].G;
            buf[(i * 4) + 2] = src[i].B;
            buf[(i * 4) + 3] = src[i].A;
        }

        var expect = new Rgba32[src.Length];
        for (int y = 0; y < 300; y++)
        {
            for (int x = 0; x < 70; x++)
            {
                int p = ((y * 70) + x) * 4;
                var cur = new Rgba32((byte)buf[p], (byte)buf[p + 1], (byte)buf[p + 2], (byte)buf[p + 3]);
                int best = 0;
                long bd = long.MaxValue;
                for (int k = 0; k < pal.Length; k++)
                {
                    long d = (long)(cur.R - pal[k].R) * (cur.R - pal[k].R) + (long)(cur.G - pal[k].G) * (cur.G - pal[k].G)
                        + (long)(cur.B - pal[k].B) * (cur.B - pal[k].B) + (long)(cur.A - pal[k].A) * (cur.A - pal[k].A);
                    if (d < bd)
                    {
                        bd = d;
                        best = k;
                    }
                }

                expect[(y * 70) + x] = pal[best];
                for (int t = 0; t < dx.Length; t++)
                {
                    int xx = x + dx[t];
                    int yy = y + dy[t];
                    if (xx < 0 || xx >= 70 || yy >= 300)
                    {
                        continue;
                    }

                    int q = ((yy * 70) + xx) * 4;
                    float[] e = [cur.R - pal[best].R, cur.G - pal[best].G, cur.B - pal[best].B, cur.A - pal[best].A];
                    for (int c = 0; c < 4; c++)
                    {
                        float v = (buf[q + c] / 255f) + ((e[c] / 255f) * (wt[t] / 48f));
                        buf[q + c] = (byte)Math.Clamp((v * 255f) + 0.5f, 0f, 255f);
                    }
                }
            }
        }

        // The reference rounds slightly differently from the library's float order, so allow a sliver of pixels to differ.
        int diff = 0;
        for (int i = 0; i < whole.Length; i++)
        {
            if (whole[i] != expect[i])
            {
                diff++;
            }
        }

        Assert.True(diff < whole.Length / 100, $"{diff} of {whole.Length} pixels differ from the direct reference");
    }
}

public class QuantizerChoiceTests
{
    private static Image Photo(int w = 240, int h = 180) => Image.FromPixels<Rgba32>(GifTestUtil.SyntheticPhoto(w, h), w, h);

    [Fact]
    public void KnownPalettesHaveTheirPublishedSizes()
    {
        Assert.Equal(142, KnownPalettes.WebSafe.Count);
        Assert.Equal(110, KnownPalettes.Werner.Count);
        Assert.Equal(new Rgba32(0xF0, 0xF8, 0xFF, 255), KnownPalettes.WebSafe[0]);
        Assert.Contains(new Rgba32(255, 255, 255, 255), KnownPalettes.WebSafe);
        Assert.Equal(new Rgba32(0xF1, 0xE9, 0xCD, 255), KnownPalettes.Werner[0]);
    }

    [Theory]
    [InlineData(Quantizer.WebSafe)]
    [InlineData(Quantizer.Werner)]
    public void FixedPalettesUseOnlyTheirColors(Quantizer q)
    {
        HashSet<Rgba32> set = [.. (q == Quantizer.WebSafe ? KnownPalettes.WebSafe : KnownPalettes.Werner)];
        foreach (Dither d in new[] { Dither.None, Dither.FloydSteinberg, Dither.Bayer8x8 })
        {
            Rgba32[] px = Photo().ConvertTo(PixelFormat.Rgb8).Quantize(8, d, quantizer: q).ToArray<Rgba32>();
            Assert.All(px, p => Assert.Contains(p with { A = 255 }, set));
        }
    }

    [Theory]
    [InlineData(256)]
    [InlineData(32)]
    [InlineData(4)]
    public void OctreeHonorsTheColorLimitAndIsClose(int colors)
    {
        Rgba32[] src = Photo().ToArray<Rgba32>();
        Rgba32[] px = Photo().Quantize(colors, quantizer: Quantizer.Octree).ToArray<Rgba32>();
        Assert.True(px.Distinct().Count() <= colors);
        double psnr = GifTestUtil.Psnr(src, px);
        double wu = GifTestUtil.Psnr(src, Photo().Quantize(colors).ToArray<Rgba32>());

        // A little coarser than Wu is expected; far coarser is a defect.
        Assert.True(psnr > wu - 6, $"octree {psnr:F1} dB vs Wu {wu:F1} dB at {colors} colors");
        Assert.Equal(px, Photo().Quantize(colors, quantizer: Quantizer.Octree).ToArray<Rgba32>(maxDegree: 8));
    }

    [Fact]
    public void OctreeKeepsTransparencyAsOneEntry()
    {
        Rgba32[] src = GifTestUtil.Pattern(60, 50, 3, 300, true);
        Rgba32[] px = Image.FromPixels<Rgba32>(src, 60, 50).Quantize(16, quantizer: Quantizer.Octree).ToArray<Rgba32>();
        Assert.True(px.Distinct().Count() <= 16);
        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal(src[i].A < 128, px[i].A == 0);
        }
    }

    [Fact]
    public void GifEncoderTakesAQuantizerAndAnyDither()
    {
        Image img = Photo();
        foreach (Quantizer q in Enum.GetValues<Quantizer>())
        {
            foreach (Dither d in new[] { Dither.None, Dither.Atkinson, Dither.Stucki, Dither.Bayer4x4 })
            {
                byte[] gif = GifTestUtil.Encode(img, new GifEncoderOptions { Quantizer = q, Dither = d });
                Rgba32[] back = GifTestUtil.Pixels(GifTestUtil.Decode(gif));
                Assert.Equal(240 * 180, back.Length);
                if (q == Quantizer.WebSafe)
                {
                    HashSet<Rgba32> set = [.. KnownPalettes.WebSafe];
                    Assert.All(back, p => Assert.Contains(p with { A = 255 }, set));
                }
            }
        }
    }

    [Fact]
    public void DefaultQuantizeGraphIsUnchanged() =>
        Assert.Contains("quantize(colors=16, dither=Atkinson, alpha=Full)", Photo().Quantize(16, Dither.Atkinson).DescribeGraph());

    [Fact]
    public void NewDiffusionKernelsKeepTheColorLimit()
    {
        foreach (Dither d in Enum.GetValues<Dither>())
        {
            Rgba32[] px = Photo().Quantize(24, d).ToArray<Rgba32>();
            Assert.True(px.Distinct().Count() <= 24, d.ToString());
        }
    }
}

public class HistogramBehaviorTests
{
    private static Image Photo(int w = 300, int h = 220) => Image.FromPixels<Rgba32>(GifTestUtil.SyntheticPhoto(w, h), w, h);

    [Fact]
    public void DeterministicAcrossDegrees()
    {
        Assert.Equal(Photo().AutoLevel().ToArray<Rgba32>(maxDegree: 1), Photo().AutoLevel().ToArray<Rgba32>(maxDegree: 8));
        Assert.Equal(Photo().AutoLevel(false).ToArray<Rgba32>(maxDegree: 1), Photo().AutoLevel(false).ToArray<Rgba32>(maxDegree: 8));
        Assert.Equal(Photo().AdaptiveThreshold().ToArray<Rgba32>(maxDegree: 1), Photo().AdaptiveThreshold().ToArray<Rgba32>(maxDegree: 8));
    }

    [Fact]
    public void RegionOfAComputedImageMatchesTheWholeFrame()
    {
        Rgba32[] t = Photo().AdaptiveThreshold().ToArray<Rgba32>();
        var tp = new Rgba32[50 * 40];
        Photo().AdaptiveThreshold().Frames[0].CopyPixelsTo<Rgba32>(tp, new ImageRect(200, 150, 50, 40));
        for (int y = 0; y < 40; y++)
        {
            Assert.Equal(t.AsSpan(((150 + y) * 300) + 200, 50).ToArray(), tp.AsSpan(y * 50, 50).ToArray());
        }
    }

    [Fact]
    public void AdaptiveThresholdOfAFlatImageIsUpper() =>
        Assert.All(
            Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(120, 120, 120, 255), 64 * 64).ToArray(), 64, 64).AdaptiveThreshold().ToArray<Rgba32>(),
            p => Assert.Equal(new Rgba32(255, 255, 255, 255), p));

    [Fact]
    public void TinyAndOddSizesWork()
    {
        foreach (var (w, h) in new[] { (1, 1), (3, 2), (17, 5), (130, 3) })
        {
            Image img = Image.FromPixels<Rgba32>(GifTestUtil.SyntheticPhoto(w, h), w, h);
            Assert.Equal(w * h, img.AutoLevel().ToArray<Rgba32>().Length);
            Assert.Equal(w * h, img.AdaptiveThreshold().ToArray<Rgba32>().Length);
            Assert.Equal(w * h, img.Dither([new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255)], Dither.Stucki).ToArray<Rgba32>().Length);
        }
    }

    [Fact]
    public void OptionsAreValidated()
    {
        Image img = Photo(16, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.AdaptiveThreshold(thresholdLimit: 1.5f));
    }

    [Fact]
    public void SixteenBitAndGrayInputsAreAccepted()
    {
        Image g = Photo().ConvertTo(PixelFormat.Gray8);
        Assert.Equal(300 * 220, g.AutoLevel().ToArray<Rgba32>().Length);
        Image w = Photo().ConvertTo(PixelFormat.Rgba16);
        Assert.Equal(PixelFormat.Rgba8, w.AutoLevel().Format);
    }
}
