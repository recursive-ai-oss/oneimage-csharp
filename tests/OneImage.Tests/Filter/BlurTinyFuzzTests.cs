using System.Diagnostics;
using Xunit;

namespace OneImage.Tests.Filter;

/// <summary>
/// Random tiny images (1 to 8 pixels per axis, now and then one long thin axis) with sigmas and radii from just past the old
/// limit to absurd, in every edge mode, through Gaussian, box and unsharp mask. Every case must finish and give finite, plausible
/// pixels; float cases whose axes are all blurred directly are also compared with a plain double-precision blur.
/// </summary>
public class BlurTinyFuzzTests
{
    private static readonly double[] s_sigmas = [0.5, 17, 49, 60, 300, 2e3, 5e4, 1e6, 1e9, 1e300];

    private static readonly int[] s_radii = [1, 49, 60, 200, 1000, 4096];

    [Fact]
    public void RandomTinyImagesFinishAndStayPlausible()
    {
        // ONEIMAGE_FUZZ_CASES / ONEIMAGE_FUZZ_SEED run a longer or different pass by hand.
        int total = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_CASES"), out int t) ? t : 3000;
        var rng = new Random(int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_SEED"), out int seed) ? seed : 20261026);
        var clock = Stopwatch.StartNew();
        int cases = 0;
        int compared = 0;
        EdgeMode[] edges = [EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Constant];
        while (cases < total)
        {
            int w = 1 + rng.Next(8);
            int h = 1 + rng.Next(8);
            if (rng.Next(4) == 0)
            {
                int longSide = 100 + rng.Next(2900);
                if (rng.Next(2) == 0)
                {
                    w = longSide;
                }
                else
                {
                    h = longSide;
                }
            }

            EdgeMode edge = edges[rng.Next(3)];
            bool linear = rng.Next(2) == 0;
            int kind = rng.Next(3);
            double sx = s_sigmas[rng.Next(s_sigmas.Length)];
            double sy = rng.Next(3) == 0 ? s_sigmas[rng.Next(s_sigmas.Length)] : sx;
            int radius = s_radii[rng.Next(s_radii.Length)];

            var pixels = new GrayFloat[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new GrayFloat((float)rng.NextDouble());
            }

            Image source = Image.FromPixels<GrayFloat>(pixels, w, h);
            if (rng.Next(3) == 0)
            {
                // Other pixel formats only have to finish and keep their size.
                Image other = rng.Next(3) switch
                {
                    0 => Image.FromPixels<Rgba32>(pixels.Select(p => new Rgba32((byte)(p.V * 255), (byte)(p.V * 99), (byte)(p.V * 7), (byte)(255 - (p.V * 100)))).ToArray(), w, h),
                    1 => Image.FromPixels<Gray16>(pixels.Select(p => new Gray16((ushort)(p.V * 65535))).ToArray(), w, h),
                    _ => Image.FromPixels<GrayAlpha8>(pixels.Select(p => new GrayAlpha8((byte)(p.V * 255), (byte)(128 + (p.V * 100)))).ToArray(), w, h),
                };
                Image blurred = kind switch
                {
                    0 => other.GaussianBlur(sx, sy, linear, edge),
                    1 => other.BoxBlur(radius, linear, edge),
                    _ => other.Sharpen(1.5, sx, 0.0, linear, edge),
                };
                Assert.Equal((w, h), (blurred.Width, blurred.Height));
                Assert.Equal(w * h, blurred.ToArray<Rgba32>().Length);
                cases++;
                continue;
            }

            Image result = kind switch
            {
                0 => source.GaussianBlur(sx, sy, linear, edge),
                1 => source.BoxBlur(radius, linear, edge),
                _ => source.Sharpen(1.5, sx, 0.0, linear, edge),
            };
            long start = clock.ElapsedMilliseconds;
            GrayFloat[] got = result.ToArray<GrayFloat>();
            Assert.True(clock.ElapsedMilliseconds - start < 20000, $"case {cases} took too long: {w}x{h} kind {kind} {sx},{sy} r{radius} {edge}");
            Assert.Equal(w * h, got.Length);
            foreach (GrayFloat p in got)
            {
                Assert.True(float.IsFinite(p.V), $"case {cases} {w}x{h} kind {kind} {sx},{sy} r{radius} {edge}: not finite");
                if (kind != 2)
                {
                    Assert.InRange(p.V, -1e-5f, 1 + 1e-5f);
                }
            }

            // A plain blur to compare with, where every axis of a Gaussian is far wider than the image (blurred directly).
            if (kind == 0 && !linear && sx > 17.0 * w && sy > 17.0 * h && sx <= 1e6 && sy <= 1e6)
            {
                var ax = BlurTinyImageTests.AxisFor(sx);
                var ay = BlurTinyImageTests.AxisFor(sy);
                double[] want = BlurTinyImageTests.Reference(pixels.Select(p => (double)p.V).ToArray(), w, h, ax, ay, edge);
                double tolerance = w > 127 || h > 127 ? 2.5e-4 : 1e-5;
                for (int i = 0; i < got.Length; i++)
                {
                    Assert.True(Math.Abs(want[i] - got[i].V) <= tolerance, $"case {cases} {w}x{h} {sx},{sy} {edge}: pixel {i} {got[i].V} want {want[i]}");
                }

                compared++;
            }

            cases++;
        }

        TestNotes.Add($"fuzz: {cases} cases, {compared} compared with a direct blur, {clock.ElapsedMilliseconds} ms");
        Assert.True(compared > 100, $"only {compared} comparisons");
    }
}
