using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.Equalize;

/// <summary>Behavior, determinism and kernel tests for histogram equalization (the ImageSharp comparison is in <see cref="EqualizeOracleTests"/>).</summary>
public class EqualizeTests
{
    private static readonly HistogramEqualizationMethod[] s_methods =
    [
        HistogramEqualizationMethod.Global,
        HistogramEqualizationMethod.AdaptiveTileInterpolation,
        HistogramEqualizationMethod.AdaptiveSlidingWindow,
    ];

    private static Image Photo(int w, int h, int seed = 3) => Image.FromPixels<Rgba32>(Gif.GifTestUtil.SyntheticPhoto(w, h, seed), w, h);

    [Fact]
    public void LumaKernelVectorPathsMatchTheScalarReference()
    {
        File.AppendAllText(
            Environment.GetEnvironmentVariable("ONEIMAGE_EQUALIZE_REPORT") ?? Path.Combine(Path.GetTempPath(), "oneimage-equalize-hw.txt"),
            $"hardware: Vector256 accelerated {Vector256.IsHardwareAccelerated}, Vector128 accelerated {Vector128.IsHardwareAccelerated}, Avx2 {Avx2.IsSupported}\n");
        var rnd = new Random(12);
        foreach (int width in new[] { 1, 7, 8, 15, 16, 17, 31, 33, 100, 257 })
        {
            foreach (bool divide in new[] { false, true })
            {
                for (int round = 0; round < 40; round++)
                {
                    byte[] px = new byte[width * 4];
                    rnd.NextBytes(px);
                    if (round % 4 == 0)
                    {
                        // Colors whose exact luma sits half way between two levels: the cases where the float operation order decides.
                        for (int i = 0; i < width; i++)
                        {
                            px[i * 4] = (byte)(i * 7);
                            px[(i * 4) + 1] = (byte)((i * 13) + 5);
                            px[(i * 4) + 2] = (byte)((i * 29) + round);
                        }
                    }

                    ushort[] expected = new ushort[width];
                    ushort[] actual = new ushort[width];
                    EqualizeKernel.LumaRgba8Scalar(px, width, expected, divide);
                    EqualizeKernel.LumaRgba8(px, width, actual, divide);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Fact]
    public void LumaKernelCoversEveryGrayLevelAndExtremes()
    {
        byte[] px = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            px[i * 4] = px[(i * 4) + 1] = px[(i * 4) + 2] = (byte)i;
            px[(i * 4) + 3] = 255;
        }

        ushort[] luma = new ushort[256];
        foreach (bool divide in new[] { false, true })
        {
            EqualizeKernel.LumaRgba8(px, 256, luma, divide);
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal(i, luma[i]);
            }
        }
    }

    [Fact]
    public void ClipAndRedistributeKeepsTheTotalAndCapsBinsAtLimitPlusShare()
    {
        var rnd = new Random(4);
        for (int round = 0; round < 200; round++)
        {
            int bins = new[] { 2, 16, 64, 256, 1000 }[round % 5];
            int[] h = new int[bins];
            for (int i = 0; i < bins; i++)
            {
                h[i] = rnd.Next(0, 3) == 0 ? rnd.Next(0, 5000) : rnd.Next(0, 30);
            }

            long total = h.Sum(v => (long)v);
            int limit = rnd.Next(1, 400);
            long excess = h.Sum(v => Math.Max(0L, v - limit));
            int[] c = (int[])h.Clone();
            EqualizeNode.ClipAndRedistribute(c, limit);
            Assert.Equal(total, c.Sum(v => (long)v));
            long share = excess / bins;
            for (int i = 0; i < bins; i++)
            {
                Assert.True(c[i] <= limit + share + 1 || h[i] < limit, $"bin {i}: {c[i]} with limit {limit}, share {share}");
                Assert.True(c[i] >= Math.Min(h[i], limit) + share);
            }
        }
    }

    [Fact]
    public void ResultsDoNotDependOnTheThreadCount()
    {
        Image img = Photo(333, 301);
        foreach (HistogramEqualizationMethod m in s_methods)
        {
            foreach (bool clip in new[] { false, true })
            {
                Image eq = img.HistogramEqualization(m, clipHistogram: clip, clipLimit: 30);
                Rgba32[] one = eq.ToArray<Rgba32>(maxDegree: 1);
                foreach (int degree in new[] { 2, 3, 8 })
                {
                    Rgba32[] many = img.HistogramEqualization(m, clipHistogram: clip, clipLimit: 30).ToArray<Rgba32>(maxDegree: degree);
                    Assert.True(one.AsSpan().SequenceEqual(many), $"{m} clip={clip} degree {degree} differs from degree 1");
                }
            }
        }
    }

    [Fact]
    public void GrayRgbAndRgbaFormatsAgreeWithTheRgbaPath()
    {
        const int W = 140, H = 90;
        Rgba32[] src = Gif.GifTestUtil.SyntheticPhoto(W, H, 5);
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = new Rgba32(src[i].R, src[i].G, src[i].B, (byte)(100 + (i % 150)));
        }

        var rgb = new Rgb24[src.Length];
        var gray = new Gray8[src.Length];
        var grayRgba = new Rgba32[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            rgb[i] = new Rgb24(src[i].R, src[i].G, src[i].B);
            gray[i] = new Gray8(src[i].G);
            grayRgba[i] = new Rgba32(src[i].G, src[i].G, src[i].G, 255);
        }

        foreach (HistogramEqualizationMethod m in s_methods)
        {
            Rgba32[] viaRgba = Image.FromPixels<Rgba32>(src, W, H).HistogramEqualization(m).ToArray<Rgba32>();
            Rgb24[] viaRgb = Image.FromPixels<Rgb24>(rgb, W, H).HistogramEqualization(m).ToArray<Rgb24>();
            Gray8[] viaGray = Image.FromPixels<Gray8>(gray, W, H).HistogramEqualization(m).ToArray<Gray8>();
            Rgba32[] grayViaRgba = Image.FromPixels<Rgba32>(grayRgba, W, H).HistogramEqualization(m).ToArray<Rgba32>();
            for (int i = 0; i < src.Length; i++)
            {
                Assert.True(viaRgba[i].R == viaRgb[i].R && viaRgba[i].B == viaRgb[i].B, $"{m} rgb vs rgba at {i % W},{i / W}: {viaRgb[i]} {viaRgba[i]}");
                Assert.True(grayViaRgba[i].R == viaGray[i].V, $"{m} gray vs rgba at {i % W},{i / W}: {viaGray[i]} {grayViaRgba[i]}");
                // Alpha is carried over untouched; color becomes gray.
                Assert.Equal(src[i].A, viaRgba[i].A);
                if (viaRgba[i] != src[i])
                {
                    Assert.Equal(viaRgba[i].R, viaRgba[i].G);
                    Assert.Equal(viaRgba[i].G, viaRgba[i].B);
                }
            }
        }
    }

    [Fact]
    public void GlobalStretchesTheRangeToZeroAndFullScaleAt8And16Bits()
    {
        var px8 = new Gray8[64 * 64];
        var px16 = new Gray16[64 * 64];
        var rnd = new Random(2);
        for (int i = 0; i < px8.Length; i++)
        {
            int v = 90 + rnd.Next(0, 40);
            px8[i] = new Gray8((byte)v);
            px16[i] = new Gray16((ushort)((v * 257) + rnd.Next(0, 200)));
        }

        Gray8[] o8 = Image.FromPixels<Gray8>(px8, 64, 64).HistogramEqualization().ToArray<Gray8>();
        Assert.Equal(0, o8.Min(p => p.V));
        Assert.Equal(255, o8.Max(p => p.V));
        Gray16[] o16 = Image.FromPixels<Gray16>(px16, 64, 64).HistogramEqualization(luminanceLevels: 65536).ToArray<Gray16>();
        Assert.Equal(0, o16.Min(p => p.V));
        Assert.Equal(65535, o16.Max(p => p.V));
        // Equalization never reorders pixels.
        for (int i = 1; i < px16.Length; i++)
        {
            int a = px16[i - 1].V.CompareTo(px16[i].V);
            int b = o16[i - 1].V.CompareTo(o16[i].V);
            Assert.True(a == 0 ? b == 0 : (a < 0 ? b <= 0 : b >= 0));
        }
    }

    [Fact]
    public void ConstantImageMapsToBlackAndFloatAndPremultipliedInputsAreAccepted()
    {
        var flat = new Rgba32[40 * 30];
        Array.Fill(flat, new Rgba32(77, 77, 77, 255));
        foreach (HistogramEqualizationMethod m in s_methods)
        {
            // (Tile interpolation leaves a few edge pixels at their input value, as ImageSharp does.)
            Rgba32[] r = Image.FromPixels<Rgba32>(flat, 40, 30).HistogramEqualization(m).ToArray<Rgba32>();
            Assert.All(r, p => Assert.True(p == new Rgba32(0, 0, 0, 255) || (m == HistogramEqualizationMethod.AdaptiveTileInterpolation && p == flat[0])));
            Assert.True(r.Count(p => p.R == 0) > r.Length - 10);
        }

        Image photo = Photo(120, 80);
        Image asFloat = photo.ConvertTo(PixelFormat.RgbaF32);
        Image eqFloat = asFloat.HistogramEqualization();
        Assert.Equal(PixelFormat.RgbaF32, eqFloat.Format);
        Image premul = photo.ConvertTo(PixelFormat.Rgba8Premultiplied);
        Assert.Equal(PixelFormat.Rgba8Premultiplied, premul.HistogramEqualization().Format);
        Rgba32[] a = photo.HistogramEqualization().ToArray<Rgba32>();
        Rgba32[] b = eqFloat.ToArray<Rgba32>();
        int maxDiff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(a[i].R - b[i].R));
        }

        // Float input is quantized to 16 bits first, so the bins can differ from the 8-bit path by a level at most.
        Assert.True(maxDiff <= 2, $"max difference {maxDiff}");
    }

    [Fact]
    public void TinyImagesAndWideFlatImagesWork()
    {
        foreach ((int w, int h) in new[] { (1, 1), (2, 2), (3, 2), (5, 1), (1, 9), (80, 33), (200, 3) })
        {
            Image img = Photo(w, h, 6);
            foreach (HistogramEqualizationMethod m in s_methods)
            {
                foreach (bool clip in new[] { false, true })
                {
                    Rgba32[] r = img.HistogramEqualization(m, clipHistogram: clip, clipLimit: 2, tiles: 8).ToArray<Rgba32>();
                    Assert.Equal(w * h, r.Length);
                }
            }
        }
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        Image img = Photo(32, 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization(luminanceLevels: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization(luminanceLevels: 65537));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization(clipLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization(tiles: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization(tiles: 1025));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization((HistogramEqualizationMethod)9));
        // A table of tiles² × levels entries that large must be refused before anything is allocated.
        Assert.Throws<ArgumentOutOfRangeException>(() => img.HistogramEqualization(HistogramEqualizationMethod.AdaptiveTileInterpolation, luminanceLevels: 65536, tiles: 1024));
        Image cmyk = img.ConvertTo(PixelFormat.Cmyk8);
        Assert.Throws<ImageOperationException>(() => cmyk.HistogramEqualization());
    }

    [Fact]
    public void LevelsQuantizeTheLuminanceBins()
    {
        Image img = Photo(64, 64);
        Rgba32[] r = img.HistogramEqualization(luminanceLevels: 16).ToArray<Rgba32>();
        Assert.True(r.Select(p => p.R).Distinct().Count() <= 16);
    }

    [Fact]
    public void DescriptorIsLanguageNeutral()
    {
        string text = Photo(16, 16).HistogramEqualization(HistogramEqualizationMethod.AdaptiveSlidingWindow, 256, true, 100, 4).DescribeGraph();
        Assert.Contains("histogram_equalization", text);
        Assert.Contains("adaptive-window", text);
        Assert.Contains("clip_limit", text);
    }

    [Fact]
    public void AnimatedImagesAreEqualizedFrameByFrame()
    {
        Image a = Photo(60, 40, 1);
        Image b = Photo(60, 40, 2);
        var anim = new Image([a.Frames[0].Node, b.Frames[0].Node], [TimeSpan.Zero, TimeSpan.Zero], 0, ImageMetadata.Empty, null);
        Image r = anim.HistogramEqualization(HistogramEqualizationMethod.AdaptiveTileInterpolation);
        Assert.Equal(2, r.FrameCount);
        Assert.True(r.Frames[1].ToArray<Rgba32>().AsSpan().SequenceEqual(b.HistogramEqualization(HistogramEqualizationMethod.AdaptiveTileInterpolation).ToArray<Rgba32>()));
    }

    [Fact]
    public void PartialRegionReadsEqualTheCorrespondingPartOfAFullRender()
    {
        Image img = Photo(300, 220);
        foreach (HistogramEqualizationMethod m in s_methods)
        {
            Image eq = img.HistogramEqualization(m, clipHistogram: true, clipLimit: 25);
            Rgba32[] full = eq.ToArray<Rgba32>();
            var rect = new ImageRect(111, 87, 140, 101);
            Rgba32[] part = new Rgba32[rect.Width * rect.Height];
            img.HistogramEqualization(m, clipHistogram: true, clipLimit: 25).CopyPixelsTo<Rgba32>(part, rect);
            for (int y = 0; y < rect.Height; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    Assert.Equal(full[((rect.Y + y) * 300) + rect.X + x], part[(y * rect.Width) + x]);
                }
            }
        }
    }

    [Fact]
    public void StreamedSourceGivesTheSameResultAsAMaterializedOne()
    {
        const int W = 1500, H = 3000;
        Func<Image> load = () => Image.Load(GenFormat.Header(W, H), new LoadOptions { Configuration = GenFormat.Instance.Configuration() });
        Assert.True(load().Frames[0].Node.Streaming);
        Image materialized = Image.FromPixels<Rgba32>(load().ToArray<Rgba32>(), W, H);
        foreach (HistogramEqualizationMethod m in s_methods)
        {
            Rgba32[] viaStream = load().HistogramEqualization(m, clipHistogram: true, clipLimit: 400).ToArray<Rgba32>();
            Rgba32[] reference = materialized.HistogramEqualization(m, clipHistogram: true, clipLimit: 400).ToArray<Rgba32>();
            Assert.True(reference.AsSpan().SequenceEqual(viaStream), $"{m}: streamed and materialized results differ");
        }
    }
}
