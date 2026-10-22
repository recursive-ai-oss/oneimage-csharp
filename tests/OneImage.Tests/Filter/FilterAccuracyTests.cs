using OneImage.Kernels;
using Xunit;
using static OneImage.Tests.Filter.FilterReference;

namespace OneImage.Tests.Filter;

/// <summary>Each filter against a whole-image double-precision reference on an image several tiles across, so tile seams are covered.</summary>
public class FilterAccuracyTests
{
    private const int W = 333;
    private const int H = 291;

    private static Image Make(byte[] raw, int channels, bool alpha, int w = W, int h = H) =>
        Resample.ResampleAccuracyTests.ImageOf(raw, w, h, Fmt(channels, alpha));

    private static byte[] Bytes(Image img, int channels, bool alpha) => Resample.ResampleAccuracyTests.ToBytes(img, Fmt(channels, alpha));

    private static void Report(string name, (int Max, double Mean) d) =>
        TestNotes.Add($"{name}: max diff {d.Max}, mean {d.Mean:F4}");

    public static TheoryData<int, bool, bool> Layouts => new()
    {
        { 4, true, true },
        { 4, true, false },
        { 3, false, true },
        { 1, false, true },
        { 2, true, true },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void GaussianMatchesReference(int ch, bool alpha, bool linear)
    {
        byte[] raw = Picture(W, H, ch, alpha);
        double[] g = Gaussian(1.7);
        double[] want = Separable(Decode(raw, ch, alpha, linear), W, H, ch, g, g, EdgeMode.Clamp);
        byte[] expected = Encode(want, ch, alpha, linear);
        byte[] got = Bytes(Make(raw, ch, alpha).GaussianBlur(1.7, linear: linear), ch, alpha);
        (int Max, double Mean) d = Diff(expected, got);
        Report($"gaussian 1.7 ch{ch} alpha{alpha} linear{linear}", d);
        Assert.True(d.Max <= 1, $"max diff {d.Max}");
    }

    [Fact]
    public void GaussianAnisotropicAndEdgeModes()
    {
        byte[] raw = Picture(W, H, 4, true);
        foreach (EdgeMode edge in new[] { EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Constant })
        {
            double[] want = Separable(Decode(raw, 4, true, true), W, H, 4, Gaussian(3.0), Gaussian(0.8), edge);
            byte[] got = Bytes(Make(raw, 4, true).GaussianBlur(3.0, 0.8, edge: edge), 4, true);
            (int Max, double Mean) d = Diff(Encode(want, 4, true, true), got);
            Report($"gaussian 3.0/0.8 {edge}", d);
            Assert.True(d.Max <= 1, $"{edge}: max diff {d.Max}");
        }
    }

    [Fact]
    public void ZeroSigmaIsIdentityAndSmallSigmaIsNearlyIdentity()
    {
        // Opaque pixels: under alpha 0 the stored color is not preserved by any premultiplied pipeline.
        byte[] raw = Picture(W, H, 3, false);
        Image img = Make(raw, 3, false);
        Assert.Same(img.Frames[0].Node, img.GaussianBlur(0).Frames[0].Node);
        Assert.Equal(raw, Bytes(img.GaussianBlur(0.05), 3, false));
        Assert.Equal(raw, Bytes(img.GaussianBlur(0.01, 0), 3, false));
        Assert.Equal(raw, Bytes(img.GaussianBlur(0.05, linear: false), 3, false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ConstantImagesStayConstant(int channels)
    {
        bool alpha = channels == 4;
        byte[] raw = new byte[W * H * channels];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i % channels == 3 ? 200 : 77 + (i % channels));
        }

        Image img = Make(raw, channels, alpha);
        Assert.Equal(raw, Bytes(img.GaussianBlur(4), channels, alpha));
        Assert.Equal(raw, Bytes(img.BoxBlur(5), channels, alpha));
        Assert.Equal(raw, Bytes(img.GaussianBlur(40), channels, alpha));
        Assert.Equal(raw, Bytes(img.Median(3), channels, alpha));
    }

    [Theory]
    [InlineData(1, EdgeMode.Clamp)]
    [InlineData(4, EdgeMode.Mirror)]
    [InlineData(9, EdgeMode.Constant)]
    public void BoxMatchesReference(int radius, EdgeMode edge)
    {
        byte[] raw = Picture(W, H, 4, true);
        double[] box = Enumerable.Repeat(1.0 / ((2 * radius) + 1), (2 * radius) + 1).ToArray();
        double[] want = Separable(Decode(raw, 4, true, true), W, H, 4, box, box, edge);
        byte[] got = Bytes(Make(raw, 4, true).BoxBlur(radius, edge: edge), 4, true);
        (int Max, double Mean) d = Diff(Encode(want, 4, true, true), got);
        Report($"box {radius} {edge}", d);
        Assert.True(d.Max <= 1, $"max diff {d.Max}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharpenMatchesReference(bool linear)
    {
        byte[] raw = Picture(W, H, 4, true);
        const double Amount = 1.6;
        const double Sigma = 1.3;
        const double Threshold = 0.01;
        double[] work = Decode(raw, 4, true, linear);
        double[] g = Gaussian(Sigma);
        double[] blur = Separable(work, W, H, 4, g, g, EdgeMode.Clamp);
        double[] outW = new double[work.Length];
        for (int i = 0; i < work.Length; i += 4)
        {
            double a = work[i + 3];
            for (int k = 0; k < 3; k++)
            {
                double o = work[i + k];
                double d = o - blur[i + k];
                double v = Math.Abs(d) < Threshold ? o : o + (Amount * d);
                outW[i + k] = Math.Clamp(v, 0, a);
            }

            outW[i + 3] = a;
        }

        byte[] got = Bytes(Make(raw, 4, true).Sharpen(Amount, Sigma, Threshold, linear), 4, true);
        byte[] wantBytes = Encode(outW, 4, true, linear);

        // A difference within float noise of the threshold may fall either side of it; leave those pixels out.
        for (int i = 0; i < work.Length; i += 4)
        {
            for (int k = 0; k < 3; k++)
            {
                if (Math.Abs(Math.Abs(work[i + k] - blur[i + k]) - Threshold) < 1e-5)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        got[i + c] = wantBytes[i + c];
                    }
                }
            }
        }

        (int Max, double Mean) d2 = Diff(wantBytes, got);
        Report($"sharpen linear{linear}", d2);
        Assert.True(d2.Max <= 1, $"max diff {d2.Max}");
    }

    [Fact]
    public void ConvolveMatchesReferenceAndSeparableMatchesGeneric()
    {
        byte[] raw = Picture(W, H, 3, false);
        Image img = Make(raw, 3, false);

        // A non-separable 5x5 kernel with negative weights, a divisor and an offset.
        float[] k5 = [0, 0, -1, 0, 0, 0, -1, -2, -1, 0, -1, -2, 17, -2, -1, 0, -1, -2, -1, 0, 0, 0, -1, 0, 0];
        double[,] ref5 = new double[5, 5];
        for (int i = 0; i < 25; i++)
        {
            ref5[i / 5, i % 5] = k5[i] / 5.0;
        }

        byte[] want = Encode(Correlate(Decode(raw, 3, false, false), W, H, 3, false, ref5, 0.1, EdgeMode.Mirror), 3, false, false);
        byte[] got = Bytes(img.Convolve(k5, 5, 5, divisor: 5, offset: 0.1, linear: false, edge: EdgeMode.Mirror), 3, false);
        (int Max, double Mean) d = Diff(want, got);
        Report("convolve 5x5", d);
        Assert.True(d.Max <= 1, $"max diff {d.Max}");

        // An asymmetric kernel pins the orientation: weight at column 2 reads the pixel to the right.
        float[] shift = [0, 0, 0, 0, 0, 1, 0, 0, 0];
        Rgb24[] px = new Rgb24[W * H];
        Make(raw, 3, false).Convolve(shift, 3, 3, linear: false).CopyPixelsTo<Rgb24>(px, new ImageRect(0, 0, W, H));
        Assert.Equal(raw[(((10 * W) + 5) * 3) + 1], px[(10 * W) + 4].G);

        // A rank-one kernel: the separable fast path and the generic path agree.
        float[] row = [1, 4, 6, 4, 1];
        float[] col = [1, 2, 3, 2, 1];
        float[] outer = new float[25];
        for (int j = 0; j < 5; j++)
        {
            for (int i = 0; i < 5; i++)
            {
                outer[(j * 5) + i] = col[j] * row[i];
            }
        }

        foreach (bool linear in new[] { true, false })
        {
            byte[] fast = Bytes(img.ConvolveCore(outer, 5, 5, 0, 0, linear, EdgeMode.Clamp, allowSeparable: true), 3, false);
            byte[] generic = Bytes(img.ConvolveCore(outer, 5, 5, 0, 0, linear, EdgeMode.Clamp, allowSeparable: false), 3, false);
            (int Max, double Mean) dd = Diff(fast, generic);
            Report($"separable vs generic linear{linear}", dd);
            Assert.True(dd.Max <= 1, $"fast vs generic: {dd.Max}");
            double[] r1 = row.Select(v => v / 16.0).ToArray();
            double[] c1 = col.Select(v => v / 9.0).ToArray();
            byte[] refb = Encode(Separable(Decode(raw, 3, false, linear), W, H, 3, r1, c1, EdgeMode.Clamp), 3, false, linear);
            Assert.True(Diff(refb, fast).Max <= 1);
        }
    }

    [Fact]
    public void ConvolveRejectsBadKernels()
    {
        Image img = Make(Picture(40, 40, 1, false), 1, false, 40, 40);
        Assert.Throws<ArgumentException>(() => img.Convolve(new float[4], 2, 2));
        Assert.Throws<ArgumentException>(() => img.Convolve(new float[8], 3, 3));
        Assert.Throws<ArgumentException>(() => img.Convolve([1f, float.NaN, 1f], 3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GaussianBlur(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GaussianBlur(1, edge: EdgeMode.Wrap));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Median(0));
    }

    [Theory]
    [InlineData(4, true, 1, EdgeMode.Clamp)]
    [InlineData(3, false, 2, EdgeMode.Mirror)]
    [InlineData(1, false, 3, EdgeMode.Constant)]
    [InlineData(2, true, 5, EdgeMode.Clamp)]
    public void MedianIsExact(int ch, bool alpha, int radius, EdgeMode edge)
    {
        const int w = 150;
        const int h = 140;
        byte[] raw = Picture(w, h, ch, alpha, seed: 9);
        byte[] want = new byte[raw.Length];
        int win = (2 * radius) + 1;
        byte[] buf = new byte[win * win];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < ch; k++)
                {
                    int n = 0;
                    for (int j = -radius; j <= radius; j++)
                    {
                        for (int i = -radius; i <= radius; i++)
                        {
                            int yy = Map(y + j, h, edge);
                            int xx = Map(x + i, w, edge);
                            buf[n++] = yy < 0 || xx < 0 ? (byte)0 : raw[((((yy * w) + xx) * ch)) + k];
                        }
                    }

                    Array.Sort(buf);
                    want[(((y * w) + x) * ch) + k] = buf[buf.Length / 2];
                }
            }
        }

        byte[] got = Bytes(Make(raw, ch, alpha, w, h).Median(radius, edge), ch, alpha);
        Assert.Equal(want, got);
    }

    [Fact]
    public void MedianOf16BitAndFloatSamples()
    {
        const int w = 140;
        const int h = 135;
        var rnd = new Random(3);
        ushort[] u16 = new ushort[w * h];
        float[] f32 = new float[w * h];
        for (int i = 0; i < u16.Length; i++)
        {
            u16[i] = (ushort)rnd.Next(65536);
            f32[i] = (float)rnd.NextDouble();
        }

        Gray16[] g16 = u16.Select(v => new Gray16(v)).ToArray();
        GrayFloat[] gf = f32.Select(v => new GrayFloat(v)).ToArray();
        Gray16[] got16 = Image.FromPixels<Gray16>(g16, w, h).Median(2).ToArray<Gray16>();
        GrayFloat[] gotF = Image.FromPixels<GrayFloat>(gf, w, h).Median(2).ToArray<GrayFloat>();
        foreach ((int x, int y) in new[] { (0, 0), (5, 7), (127, 127), (128, 128), (139, 134), (60, 130) })
        {
            List<ushort> a = [];
            List<float> b = [];
            for (int j = -2; j <= 2; j++)
            {
                for (int i = -2; i <= 2; i++)
                {
                    int yy = Math.Clamp(y + j, 0, h - 1);
                    int xx = Math.Clamp(x + i, 0, w - 1);
                    a.Add(u16[(yy * w) + xx]);
                    b.Add(f32[(yy * w) + xx]);
                }
            }

            a.Sort();
            b.Sort();
            Assert.Equal(a[12], got16[(y * w) + x].V);
            Assert.Equal(b[12], gotF[(y * w) + x].V);
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(1, false)]
    [InlineData(4, true)]
    public void SobelMatchesReference(int ch, bool alpha)
    {
        byte[] raw = Picture(W, H, ch, alpha);
        double[] luma = new double[W * H];
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = ch == 1 ? raw[i] / 255.0 : ((0.2126 * raw[i * ch]) + (0.7152 * raw[(i * ch) + 1]) + (0.0722 * raw[(i * ch) + 2])) / 255.0;
        }

        byte[] want = new byte[W * H];
        const double Scale = 1.5;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double P(int dx, int dy) => luma[(Map(y + dy, H, EdgeMode.Clamp) * W) + Map(x + dx, W, EdgeMode.Clamp)];
                double gx = (P(1, -1) - P(-1, -1)) + (2 * (P(1, 0) - P(-1, 0))) + (P(1, 1) - P(-1, 1));
                double gy = (P(-1, 1) + (2 * P(0, 1)) + P(1, 1)) - (P(-1, -1) + (2 * P(0, -1)) + P(1, -1));
                double m = Math.Sqrt((gx * gx) + (gy * gy)) * Scale / 4;
                want[(y * W) + x] = (byte)Math.Clamp(Math.Floor((m * 255) + 0.5), 0, 255);
            }
        }

        Image sobel = Make(raw, ch, alpha).Sobel(Scale);
        Assert.Equal(PixelFormat.Gray8, sobel.Format);
        byte[] got = Resample.ResampleAccuracyTests.ToBytes(sobel, PixelFormat.Gray8);
        (int Max, double Mean) d = Diff(want, got);
        Report($"sobel ch{ch}", d);
        Assert.True(d.Max <= 1, $"max diff {d.Max}");
    }

    [Fact]
    public void SobelRefusesCmyk()
    {
        Image cmyk = Resample.ResampleAccuracyTests.ImageOf(new byte[16 * 16 * 4], 16, 16, PixelFormat.Cmyk8);
        Assert.Throws<ImageOperationException>(() => cmyk.Sobel());
    }

    [Fact]
    public void TransparentObjectBlursWithoutHalo()
    {
        // A solid red disc on fully transparent black-ish background: the stored color under alpha 0 is blue garbage.
        const int n = 200;
        byte[] raw = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = ((y * n) + x) * 4;
                bool inside = ((x - 100) * (x - 100)) + ((y - 100) * (y - 100)) < 50 * 50;
                raw[i] = (byte)(inside ? 255 : 0);
                raw[i + 1] = 0;
                raw[i + 2] = (byte)(inside ? 0 : 255);
                raw[i + 3] = (byte)(inside ? 255 : 0);
            }
        }

        foreach (bool linear in new[] { true, false })
        {
            Rgba32[] px = Make(raw, 4, true, n, n).GaussianBlur(4, linear: linear).ToArray<Rgba32>();
            int partial = 0;
            foreach (Rgba32 p in px)
            {
                if (p.A is > 0 and < 255)
                {
                    partial++;

                    // Un-premultiplied color of every partly transparent pixel is the object's red; no dark or blue fringe.
                    Assert.True(p.R >= 253 && p.G <= 1 && p.B <= 1, $"fringe color {p} (linear {linear})");
                }
            }

            Assert.True(partial > 500);
        }
    }

    [Fact]
    public void ResultsDoNotDependOnThreadCount()
    {
        byte[] raw = Picture(W, H, 4, true);
        foreach (Func<Image, Image> op in new Func<Image, Image>[] { i => i.GaussianBlur(2.5), i => i.Sharpen(), i => i.Median(2), i => i.Sobel(), i => i.GaussianBlur(30) })
        {
            Image a = op(Make(raw, 4, true));
            Image b = op(Make(raw, 4, true));
            Rgba32[] one = new Rgba32[a.Width * a.Height];
            Rgba32[] many = new Rgba32[a.Width * a.Height];
            if (a.Format == PixelFormat.Rgba8)
            {
                a.CopyPixelsTo<Rgba32>(one, new ImageRect(0, 0, a.Width, a.Height), maxDegree: 1);
                b.CopyPixelsTo<Rgba32>(many, new ImageRect(0, 0, b.Width, b.Height), maxDegree: 8);
                Assert.True(one.AsSpan().SequenceEqual(many));
            }
            else
            {
                Assert.True(a.ToArray<Gray8>(maxDegree: 1).AsSpan().SequenceEqual(b.ToArray<Gray8>(maxDegree: 8)));
            }
        }
    }

    [Fact]
    public void WideBlurApproximationStaysClose()
    {
        const int w = 400;
        const int h = 360;
        byte[] raw = Picture(w, h, 3, false);
        foreach (double sigma in new[] { 20.0, 45.0 })
        {
            double[] g = Gaussian(sigma);
            byte[] want = Encode(Separable(Decode(raw, 3, false, true), w, h, 3, g, g, EdgeMode.Clamp), 3, false, true);
            byte[] got = Bytes(Make(raw, 3, false, w, h).GaussianBlur(sigma), 3, false);
            (int Max, double Mean) d = Diff(want, got);
            Report($"wide gaussian sigma {sigma} vs exact", d);
            Assert.True(d.Mean < 1.0 && d.Max <= 14, $"sigma {sigma}: mean {d.Mean}, max {d.Max}");
        }

        double[] box = Enumerable.Repeat(1.0 / 121, 121).ToArray();
        byte[] boxWant = Encode(Separable(Decode(raw, 3, false, true), w, h, 3, box, box, EdgeMode.Clamp), 3, false, true);
        (int Max, double Mean) bd = Diff(boxWant, Bytes(Make(raw, 3, false, w, h).BoxBlur(60), 3, false));
        Report("wide box radius 60 vs exact", bd);
        Assert.True(bd.Mean < 1.0 && bd.Max <= 8, $"box: mean {bd.Mean}, max {bd.Max}");
    }

    [Fact]
    public void AnimatedImagesFilterEveryFrame()
    {
        byte[] a = Picture(60, 50, 4, true, 1);
        byte[] b = Picture(60, 50, 4, true, 2);
        Node n1 = new Resample.RawBytesNode(a, 60, 50, PixelFormat.Rgba8);
        Node n2 = new Resample.RawBytesNode(b, 60, 50, PixelFormat.Rgba8);
        var anim = new Image([n1, n2], [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80)], 0, ImageMetadata.Empty, null);
        Image blurred = anim.GaussianBlur(2);
        Assert.Equal(2, blurred.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(80), blurred.Frames[1].Delay);
        Assert.True(blurred.Frames[1].ToImage().ToArray<Rgba32>().AsSpan().SequenceEqual(Make(b, 4, true, 60, 50).GaussianBlur(2).ToArray<Rgba32>()));
    }

    [Fact]
    public void DescriptorsAreLanguageNeutral()
    {
        Image img = Make(Picture(40, 40, 4, true), 4, true, 40, 40);
        Assert.Equal("gaussian_blur(sigma_x=2, sigma_y=2, radius_x=6, radius_y=6, linear=True, edge=clamp)", img.GaussianBlur(2).Frames[0].Node.Descriptor.ToString());
        Assert.Equal("median(radius=2, edge=mirror)", img.Median(2, EdgeMode.Mirror).Frames[0].Node.Descriptor.ToString());
        Assert.StartsWith("convolve(width=3, height=1, kernel=1,2,1", img.Convolve([1f, 2f, 1f], 3, 1).Frames[0].Node.Descriptor.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(130)]
    [InlineData(512)]
    public void KernelsVectorEqualsScalarBitForBit(int n)
    {
        TestNotes.Add($"Vector.IsHardwareAccelerated={System.Numerics.Vector.IsHardwareAccelerated}, Vector<float>.Count={System.Numerics.Vector<float>.Count}, Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        var rnd = new Random(n);
        float[] src = Enumerable.Range(0, n + 200).Select(_ => (float)rnd.NextDouble()).ToArray();
        int[] offs = [0, 3, 40, 100, 200, 77];
        float[] w = [0.1f, -0.7f, 0.33f, 1.3f, 0.01f, 0.5f];
        float[] a = new float[n];
        float[] b = new float[n];
        FilterKernel.SumTaps(src, offs, w, a);
        FilterKernel.SumTapsScalar(src, offs, w, b);
        Assert.Equal(b, a);

        int[] offA = [0, 10, 20];
        int[] offB = [200, 150, 100];
        float[] pw = [0.2f, 0.3f, 0.4f];
        FilterKernel.SumPairs(src, 60, 0.9f, offA, offB, pw, a);
        FilterKernel.SumPairsScalar(src, 60, 0.9f, offA, offB, pw, b);
        Assert.Equal(b, a);

        Assert.Throws<ArgumentException>(() => FilterKernel.SumTaps(src, [src.Length - n + 1], [1f], a));

        float[] r0 = Enumerable.Range(0, n + 2).Select(_ => (float)rnd.NextDouble()).ToArray();
        float[] r1 = Enumerable.Range(0, n + 2).Select(_ => (float)rnd.NextDouble()).ToArray();
        float[] r2 = Enumerable.Range(0, n + 2).Select(_ => (float)rnd.NextDouble()).ToArray();
        FilterRankKernel.SobelRow(r0, r1, r2, 0.25f, a);
        for (int x = 0; x < n; x++)
        {
            float gx = (r0[x + 2] - r0[x]) + ((r1[x + 2] - r1[x]) * 2f) + (r2[x + 2] - r2[x]);
            float gy = (r2[x] + (r2[x + 1] * 2f) + r2[x + 2]) - (r0[x] + (r0[x + 1] * 2f) + r0[x + 2]);
            Assert.Equal(MathF.Sqrt((gx * gx) + (gy * gy)) * 0.25f, a[x]);
        }
    }
}
