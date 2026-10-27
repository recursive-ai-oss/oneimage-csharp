using Xunit;
using static OneImage.Tests.Filter.FilterReference;

namespace OneImage.Tests.Filter;

/// <summary>
/// Gaussian blur, box blur and unsharp mask on images only a few pixels across (or one long thin axis) with a sigma or radius
/// far larger than the image: they must finish and agree with a direct blur written from the documented kernel.
/// </summary>
public class BlurTinyImageTests
{
    /// <summary>An independent double-precision blur of one line: the kernel is applied tap by tap as documented, with the edge mode read at every tap.</summary>
    internal sealed class Axis
    {
        private static readonly Dictionary<(double, int), Axis> s_cache = [];

        public static Axis Of(double sigma, int boxRadius)
        {
            lock (s_cache)
            {
                if (!s_cache.TryGetValue((sigma, boxRadius), out Axis? a))
                {
                    s_cache[(sigma, boxRadius)] = a = new Axis(sigma, boxRadius);
                }

                return a;
            }
        }

        private readonly double[] _w;
        private readonly double[] _cum;
        private readonly long _r;
        private readonly double _total;

        public Axis(double sigma, int boxRadius)
        {
            _r = boxRadius > 0 ? boxRadius : Math.Max(1, (long)Math.Ceiling(3 * sigma));
            _w = new double[(2 * _r) + 1];
            _cum = new double[_w.Length];
            double sum = 0;
            for (long t = 0; t < _w.Length; t++)
            {
                long k = t - _r;
                _w[t] = boxRadius > 0 ? 1 : Math.Exp(-(double)k * k / (2 * sigma * sigma));
                sum += _w[t];
                _cum[t] = sum;
            }

            _total = sum;
        }

        private double Weight(long k) => Math.Abs(k) > _r ? 0 : _w[k + _r];

        private double Upto(long k) => k < -_r ? 0 : k >= _r ? _total : _cum[k + _r];

        private readonly Dictionary<int, double[]> _classes = [];

        // Taps that read the same image pixel are 2n apart; their weights add up once per line length.
        private double[] Classes(int n)
        {
            if (!_classes.TryGetValue(n, out double[]? classes))
            {
                classes = new double[2 * n];
                for (long t = 0; t < _w.Length; t++)
                {
                    long k = t - _r;
                    classes[(int)(((k % (2 * n)) + (2 * n)) % (2 * n))] += _w[t];
                }

                _classes[n] = classes;
            }

            return classes;
        }

        public double[] Blur(double[] p, EdgeMode edge)
        {
            int n = p.Length;
            var o = new double[n];
            if (n == 1)
            {
                o[0] = edge == EdgeMode.Constant ? p[0] * Weight(0) / _total : p[0];
                return o;
            }

            double[]? classes = edge == EdgeMode.Mirror ? Classes(n) : null;

            for (int i = 0; i < n; i++)
            {
                double s = 0;
                if (edge == EdgeMode.Clamp)
                {
                    s += Upto(-i) * p[0];
                    s += (_total - Upto(n - 2 - i)) * p[n - 1];
                    for (int j = 1; j < n - 1; j++)
                    {
                        s += Weight(j - i) * p[j];
                    }
                }
                else if (edge == EdgeMode.Constant)
                {
                    for (int j = 0; j < n; j++)
                    {
                        s += Weight(j - i) * p[j];
                    }
                }
                else
                {
                    for (int j = 0; j < n; j++)
                    {
                        s += p[j] * (classes![(((j - i) % (2 * n)) + (2 * n)) % (2 * n)] + classes[(((2 * n) - 1 - j - i) % (2 * n) + (2 * n)) % (2 * n)]);
                    }
                }

                o[i] = s / _total;
            }

            return o;
        }
    }

    /// <summary>Blurs interleaved working values (separable, x then y).</summary>
    internal static Axis AxisFor(double sigma) => Axis.Of(sigma, 0);

    internal static double[] Reference(double[] v, int w, int h, Axis ax, Axis ay, EdgeMode edge) => Blur2D(v, w, h, 1, ax, ay, edge);

    private static double[] Blur2D(double[] v, int w, int h, int ch, Axis ax, Axis ay, EdgeMode edge)
    {
        var t = (double[])v.Clone();
        for (int c = 0; c < ch; c++)
        {
            for (int y = 0; y < h; y++)
            {
                var line = new double[w];
                for (int x = 0; x < w; x++)
                {
                    line[x] = v[((((y * w) + x)) * ch) + c];
                }

                double[] r = ax.Blur(line, edge);
                for (int x = 0; x < w; x++)
                {
                    t[((((y * w) + x)) * ch) + c] = r[x];
                }
            }
        }

        var o = (double[])t.Clone();
        for (int c = 0; c < ch; c++)
        {
            for (int x = 0; x < w; x++)
            {
                var line = new double[h];
                for (int y = 0; y < h; y++)
                {
                    line[y] = t[((((y * w) + x)) * ch) + c];
                }

                double[] r = ay.Blur(line, edge);
                for (int y = 0; y < h; y++)
                {
                    o[((((y * w) + x)) * ch) + c] = r[y];
                }
            }
        }

        return o;
    }

    private static double[] Noise(int count, int seed)
    {
        var rng = new Random(seed);
        var v = new double[count];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = rng.Next(3) == 0 ? rng.Next(2) : rng.NextDouble();
        }

        return v;
    }

    // Photograph-like content (slow waves and a little noise) for the cases where one axis still goes through the reduced-resolution
    // approximation, whose documented tolerance is stated for such content.
    private static double[] Smooth(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var v = new double[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                v[(y * w) + x] = 0.5 + (0.3 * Math.Sin((x * 0.011) + (y * 0.017))) + (0.1 * (rng.NextDouble() - 0.5));
            }
        }

        return v;
    }

    public static readonly (int W, int H)[] TinySizes = [(1, 1), (1, 37), (37, 1), (2, 2), (3, 3), (5, 40), (40, 5), (7, 1), (127, 3), (3, 129)];

    public static TheoryData<int, int, EdgeMode> TinyCases()
    {
        var d = new TheoryData<int, int, EdgeMode>();
        foreach ((int w, int h) in TinySizes)
        {
            foreach (EdgeMode e in new[] { EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Constant })
            {
                d.Add(w, h, e);
            }
        }

        return d;
    }

    // Sigmas more than 16 times the longest side used here, up to 1e6 (a kernel of six million taps).
    private static readonly double[] s_sigmas = [2200, 1e4, 1e6];

    private static Axis G(double sigma) => sigma == 0 ? Axis.Of(1e-9, 0) : Axis.Of(sigma, 0);

    // Where every axis is blurred directly the only error is float rounding (or the documented flat-kernel error on an axis longer
    // than 127 pixels); an axis the reduction still handles carries that approximation's error (worst pixel about 14 levels).
    // A blur the reduction still finishes is the earlier approximation, unchanged: it collapses a short axis to one pixel before blurring,
    // which loses the edge-dominated look of a clamped wide window; on smooth content that is within about 0.1 of full scale.
    private const double NearTolerance = 0.13;

    private static double Tolerance(int w, int h, double sx, double sy)
    {
        bool direct = (sx == 0 || sx > 17.0 * w) && (sy == 0 || sy > 17.0 * h);
        if (!direct)
        {
            return 14.0 / 255;
        }

        return w > 127 || h > 127 ? 2.5e-4 : 1e-5;
    }

    private static void AssertClose(double[] want, double[] got, double tolerance, string what)
    {
        Assert.Equal(want.Length, got.Length);
        double worst = 0;
        for (int i = 0; i < want.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(want[i] - got[i]));
        }

        TestNotes.Add($"{what}: max diff {worst:G3}");
        Assert.True(worst <= tolerance, $"{what}: max diff {worst} > {tolerance}");
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public void GaussianFloatMatchesDirectBlur(int w, int h, EdgeMode edge)
    {
        double[] v = Noise(w * h, w + (h * 1000));
        Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
        foreach (double sigma in s_sigmas)
        {
            var ax = Axis.Of(sigma, 0);
            double[] want = Blur2D(v, w, h, 1, ax, ax, edge);
            double[] got = img.GaussianBlur(sigma, null, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray();
            AssertClose(want, got, Tolerance(w, h, sigma, sigma), $"gaussian {w}x{h} sigma {sigma} {edge}");
        }
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public void GaussianAnisotropicMatchesDirectBlur(int w, int h, EdgeMode edge)
    {
        foreach ((double sx, double sy) in new[] { (3000.0, 0.0), (0.0, 3000.0), (2500.0, 2.0), (2.0, 5000.0) })
        {
            bool direct = Tolerance(w, h, sx, sy) < 0.05;
            double[] v = direct ? Noise(w * h, 3 + w + (h * 1000)) : Smooth(w, h, 3 + w + (h * 1000));
            Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
            double[] want = Blur2D(v, w, h, 1, G(sx), G(sy), edge);
            double[] got = img.GaussianBlur(sx, sy, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray();
            AssertClose(want, got, Tolerance(w, h, sx, sy), $"gaussian {w}x{h} sigma {sx},{sy} {edge}");
        }
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public void BoxFloatMatchesDirectBlur(int w, int h, EdgeMode edge)
    {
        foreach (int radius in new[] { 49, 500, 4096 })
        {
            bool direct = radius > 48.0 * Math.Max(w, h);
            double[] v = direct ? Noise(w * h, 11 + w + (h * 1000)) : Smooth(w, h, 11 + w + (h * 1000));
            Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
            var ax = Axis.Of(0, radius);
            double[] want = Blur2D(v, w, h, 1, ax, ax, edge);
            double[] got = img.BoxBlur(radius, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray();
            // The window covers the image many times over only when the radius is more than 48 times its longer side.
            AssertClose(want, got, radius > 48.0 * Math.Max(w, h) ? (Math.Max(w, h) > 127 ? 2.5e-4 : 1e-5) : NearTolerance, $"box {w}x{h} radius {radius} {edge}");
        }
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public void SharpenFloatMatchesDirectBlur(int w, int h, EdgeMode edge)
    {
        double[] v = Noise(w * h, 23 + w + (h * 1000));
        Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
        foreach (double sigma in new[] { 2200.0, 1e4 })
        {
            var ax = Axis.Of(sigma, 0);
            double[] blur = Blur2D(v, w, h, 1, ax, ax, edge);
            double[] want = v.Select((x, i) => x + (1.7 * (x - blur[i]))).ToArray();
            double[] got = img.Sharpen(1.7, sigma, 0, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray();
            AssertClose(want, got, Tolerance(w, h, sigma, sigma) * 2.5, $"sharpen {w}x{h} sigma {sigma} {edge}");
        }
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public void EightBitRgbaInLinearLightMatchesDirectBlur(int w, int h, EdgeMode edge)
    {
        var rng = new Random(w + (h * 77));
        var pixels = new Rgba32[w * h];
        byte[] raw = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)(rng.Next(3) == 0 ? rng.Next(256) : 255));
            raw[(i * 4)] = pixels[i].R;
            raw[(i * 4) + 1] = pixels[i].G;
            raw[(i * 4) + 2] = pixels[i].B;
            raw[(i * 4) + 3] = pixels[i].A;
        }

        Image img = Image.FromPixels<Rgba32>(pixels, w, h);
        double[] work = Decode(raw, 4, true, true);
        foreach (double sigma in new[] { 2200.0, 1e6 })
        {
            var ax = Axis.Of(sigma, 0);
            byte[] want = Encode(Blur2D(work, w, h, 4, ax, ax, edge), 4, true, true);
            Rgba32[] got = img.GaussianBlur(sigma, null, linear: true, edge: edge).ToArray<Rgba32>();
            int max = 0;
            for (int i = 0; i < got.Length; i++)
            {
                max = Math.Max(max, Math.Abs(got[i].R - want[i * 4]));
                max = Math.Max(max, Math.Abs(got[i].G - want[(i * 4) + 1]));
                max = Math.Max(max, Math.Abs(got[i].B - want[(i * 4) + 2]));
                max = Math.Max(max, Math.Abs(got[i].A - want[(i * 4) + 3]));
            }

            TestNotes.Add($"rgba32 {w}x{h} sigma {sigma} {edge}: max diff {max}");
            Assert.True(max <= (w > 127 || h > 127 ? 1 : 1), $"rgba32 {w}x{h} sigma {sigma} {edge}: max diff {max}");
        }

        if (4096 <= 48 * Math.Max(w, h))
        {
            return;
        }

        var boxAxis = Axis.Of(0, 4096);
        byte[] boxWant = Encode(Blur2D(work, w, h, 4, boxAxis, boxAxis, edge), 4, true, true);
        Rgba32[] boxGot = img.BoxBlur(4096, linear: true, edge: edge).ToArray<Rgba32>();
        for (int i = 0; i < boxGot.Length; i++)
        {
            Assert.True(
                Math.Abs(boxGot[i].R - boxWant[i * 4]) <= 1 && Math.Abs(boxGot[i].G - boxWant[(i * 4) + 1]) <= 1 && Math.Abs(boxGot[i].B - boxWant[(i * 4) + 2]) <= 1 && Math.Abs(boxGot[i].A - boxWant[(i * 4) + 3]) <= 1,
                $"box {w}x{h} {edge} pixel {i}");
        }
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public void SixteenBitGrayMatchesDirectBlur(int w, int h, EdgeMode edge)
    {
        var rng = new Random(w + (h * 5));
        var pixels = new Gray16[w * h];
        double[] v = new double[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Gray16((ushort)rng.Next(65536));
            v[i] = pixels[i].V / 65535.0;
        }

        Image img = Image.FromPixels<Gray16>(pixels, w, h);
        var ax = Axis.Of(3000, 0);
        double[] want = Blur2D(v, w, h, 1, ax, ax, edge);
        Gray16[] got = img.GaussianBlur(3000, null, linear: false, edge: edge).ToArray<Gray16>();
        double tolerance = Math.Max(1.5 / 65535, Tolerance(w, h, 3000, 3000));
        for (int i = 0; i < got.Length; i++)
        {
            Assert.True(Math.Abs((got[i].V / 65535.0) - want[i]) <= tolerance, $"gray16 {w}x{h} {edge} pixel {i}");
        }
    }

    // Long thin images: one axis far longer than the sigma allows folding, the other tiny. The long axis uses the closed form,
    // which treats the kernel as flat across the image; its documented error is under 0.1 level at 8 bits (2.5e-4 of full scale).
    [Theory]
    [InlineData(3, 10000)]
    [InlineData(10000, 3)]
    [InlineData(1, 5000)]
    [InlineData(5000, 1)]
    public void LongThinImagesMatchDirectBlur(int w, int h)
    {
        foreach (EdgeMode edge in new[] { EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Constant })
        {
            foreach (double sigma in new[] { 200.0, 1e3, 1e6 })
            {
                // The long axis is only reduced (approximated) below sigma = 16 times its length.
                bool direct = sigma > 16.0 * Math.Max(w, h);
                double[] v = direct ? Noise(w * h, w + h) : Smooth(w, h, w + h);
                Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
                var ax = Axis.Of(sigma, 0);
                double[] want = Blur2D(v, w, h, 1, ax, ax, edge);
                double[] got = img.GaussianBlur(sigma, null, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray();
                AssertClose(want, got, sigma >= 1e6 ? 2.5e-4 : 14.0 / 255, $"gaussian {w}x{h} sigma {sigma} {edge}");
            }

            bool directBox = 4096 > 48 * Math.Max(w, h);
            double[] v2 = directBox ? Noise(w * h, w + h + 1) : Smooth(w, h, w + h + 1);
            Image img2 = Image.FromPixels<GrayFloat>(v2.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
            var box = Axis.Of(0, 4096);
            AssertClose(
                Blur2D(v2, w, h, 1, box, box, edge),
                img2.BoxBlur(4096, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray(),
                directBox ? 2.5e-4 : NearTolerance,
                $"box {w}x{h} radius 4096 {edge}");
            var sh = Axis.Of(1e6, 0);
            double[] blur = Blur2D(v2, w, h, 1, sh, sh, edge);
            AssertClose(
                v2.Select((x, i) => x + (2 * (x - blur[i]))).ToArray(),
                img2.Sharpen(2, 1e6, 0, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray(),
                5e-4,
                $"sharpen {w}x{h} sigma 1e6 {edge}");
        }
    }

    // The closed form is least accurate just past the point where it takes over (sigma a little over 16 times the length), on an
    // axis too long to fold: its documented error there is under 2.5e-4 of full scale.
    [Theory]
    [InlineData(200, 1)]
    [InlineData(1, 300)]
    [InlineData(400, 3)]
    [InlineData(2, 600)]
    public void ClosedFormIsWithinItsBoundJustPastTheThreshold(int w, int h)
    {
        double[] v = Noise(w * h, 99 + w + h);
        Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), w, h);
        double sigma = 16.5 * Math.Max(w, h);
        foreach (EdgeMode edge in new[] { EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Constant })
        {
            var ax = Axis.Of(sigma, 0);
            double[] want = Blur2D(v, w, h, 1, ax, ax, edge);
            double[] got = img.GaussianBlur(sigma, null, linear: false, edge: edge).ToArray<GrayFloat>().Select(p => (double)p.V).ToArray();
            AssertClose(want, got, 2.5e-4, $"closed form {w}x{h} sigma {sigma} {edge}");
        }
    }

    [Fact]
    public void AstronomicalSigmaAndSingleAxisCasesFinish()
    {
        double[] v = Noise(12 * 5, 4);
        Image img = Image.FromPixels<GrayFloat>(v.Select(x => new GrayFloat((float)x)).ToArray(), 12, 5);
        double mean = v.Average();
        foreach (double sigma in new[] { 1e12, 1e100, 1e300, double.MaxValue })
        {
            GrayFloat[] mirror = img.GaussianBlur(sigma, null, linear: false, edge: EdgeMode.Mirror).ToArray<GrayFloat>();
            Assert.All(mirror, p => Assert.InRange(p.V, mean - 2e-3, mean + 2e-3));
            Assert.All(img.GaussianBlur(sigma, null, linear: false, edge: EdgeMode.Clamp).ToArray<GrayFloat>(), p => Assert.InRange(p.V, 0f, 1f));
            Assert.All(img.GaussianBlur(sigma, null, linear: false, edge: EdgeMode.Constant).ToArray<GrayFloat>(), p => Assert.InRange(p.V, 0f, 1e-6f));
            Assert.Equal(60, img.Sharpen(1, sigma, 0, linear: false).ToArray<GrayFloat>().Length);
        }
    }

    [Fact]
    public void AnimatedTinyFramesAreBlurredPerFrame()
    {
        Node n1 = new Resample.RawBytesNode([10, 200, 30], 3, 1, PixelFormat.Gray8);
        Node n2 = new Resample.RawBytesNode([250, 5, 90], 3, 1, PixelFormat.Gray8);
        var anim = new Image([n1, n2], [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40)], 0, ImageMetadata.Empty, null);
        Image blurred = anim.GaussianBlur(500, null, linear: false);
        Assert.Equal(2, blurred.FrameCount);
        Image second = Image.FromPixels<Gray8>([new Gray8(250), new Gray8(5), new Gray8(90)], 3, 1).GaussianBlur(500, null, linear: false);
        Assert.True(blurred.Frames[1].ToImage().ToArray<Gray8>().AsSpan().SequenceEqual(second.ToArray<Gray8>()));
        Assert.NotEqual(blurred.Frames[0].ToImage().ToArray<Gray8>()[0], blurred.Frames[1].ToImage().ToArray<Gray8>()[0]);
    }
}
