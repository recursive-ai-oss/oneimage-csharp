using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;

namespace OneImage.Tests;

/// <summary>
/// RGB to gray through the integer path must equal the float reference for every input: all 2^24 RGB triples at each target (and every
/// alpha value), at row widths that exercise vector bodies and tails.
/// </summary>
public class RgbToGrayTests
{
    private static readonly PixelFormat GrayAlpha16 = new(ColorModel.Gray, SampleType.U16, AlphaMode.Straight);
    private static readonly int[] s_widths = [4096, 4093, 1, 7, 8, 15, 16, 17, 31, 33, 63, 64, 65, 1000];

    // Every red value by default; ONEIMAGE_GRAY_RED_STEP=n tests every n-th red value (for slow emulated runs).
    private static readonly int Step = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_GRAY_RED_STEP"), out int step) && step is >= 1 and <= 255 ? step : 1;

    public static TheoryData<string> Targets => new() { "Gray8", "Gray16", "GrayAlpha8", "GrayAlpha16" };

    private static PixelFormat Target(string name) => name switch
    {
        "Gray8" => PixelFormat.Gray8,
        "Gray16" => PixelFormat.Gray16,
        "GrayAlpha8" => PixelFormat.GrayAlpha8,
        _ => GrayAlpha16,
    };

    /// <summary>Rgb24 (no alpha; the target's alpha, if any, is opaque): all 16,777,216 triples.</summary>
    [Theory]
    [MemberData(nameof(Targets))]
    public void Rgb24AllTriplesMatchFloatPath(string target)
    {
        PixelFormat to = Target(target);
        long bad = Sweep(PixelFormat.Rgb8, to, alphaPattern: false);
        Assert.Equal(0, bad);
    }

    /// <summary>Rgba32 straight: all triples, with the alpha cycling through all 256 values so every alpha meets many colors.</summary>
    [Theory]
    [MemberData(nameof(Targets))]
    public void Rgba32AllTriplesAndAllAlphasMatchFloatPath(string target)
    {
        PixelFormat to = Target(target);
        long bad = Sweep(PixelFormat.Rgba8, to, alphaPattern: true);
        Assert.Equal(0, bad);
    }

    /// <summary>Premultiplied sources: every color triple at several alphas, including 0, 1 and 255.</summary>
    [Theory]
    [MemberData(nameof(Targets))]
    public void PremultipliedMatchesFloatPath(string target)
    {
        PixelFormat to = Target(target);
        long bad = 0;
        foreach (int alpha in new[] { 0, 1, 2, 77, 128, 200, 254, 255 })
        {
            bad += SweepFixedAlpha(PixelFormat.Rgba8Premultiplied, to, (byte)alpha);
        }

        Assert.Equal(0, bad);
    }

    /// <summary>The vector rows equal the scalar per-pixel reference at awkward widths and offsets.</summary>
    [Fact]
    public void VectorRowsEqualScalarPixels()
    {
        TestContext.Current.SendDiagnosticMessage($"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} Avx2={Avx2.IsSupported} Avx512F={Avx512F.IsSupported}");
        var rnd = new Random(11);
        foreach (int width in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 47, 48, 49, 100, 255, 1025 })
        {
            foreach (bool alpha in new[] { false, true })
            {
                int bpp = alpha ? 4 : 3;
                var src = new byte[(width * bpp) + 5];
                rnd.NextBytes(src);
                for (int offset = 0; offset < 5; offset++)
                {
                    ReadOnlySpan<byte> row = src.AsSpan(offset, width * bpp);
                    var g8 = new byte[width];
                    var g16 = new ushort[width];
                    RgbToGrayKernel.Gray8Row(row, alpha, g8, width);
                    RgbToGrayKernel.Gray16Row(row, alpha, g16, width);
                    for (int x = 0; x < width; x++)
                    {
                        Assert.Equal(RgbToGrayKernel.Gray8Pixel(row[x * bpp], row[(x * bpp) + 1], row[(x * bpp) + 2]), g8[x]);
                        Assert.Equal(RgbToGrayKernel.Gray16Pixel(row[x * bpp], row[(x * bpp) + 1], row[(x * bpp) + 2]), g16[x]);
                    }
                }
            }
        }
    }

    private static long Sweep(PixelFormat from, PixelFormat to, bool alphaPattern)
    {
        long bad = 0;
        Parallel.For(0, 256 / Step, new ParallelOptions { MaxDegreeOfParallelism = 8 }, slab =>
        {
            int r = slab * Step;
            int sch = from.ChannelCount;
            int dch = to.ChannelCount;
            int dbpp = to.BytesPerPixel;
            var src = new byte[65536 * sch];
            for (int i = 0; i < 65536; i++)
            {
                src[i * sch] = (byte)r;
                src[(i * sch) + 1] = (byte)(i >> 8);
                src[(i * sch) + 2] = (byte)i;
                if (sch == 4)
                {
                    src[(i * sch) + 3] = alphaPattern ? (byte)(i + (r * 37)) : (byte)255;
                }
            }

            long local = Compare(src, from, to, 65536, dbpp, r);
            if (local != 0)
            {
                Interlocked.Add(ref bad, local);
            }
        });
        return bad;
    }

    private static long SweepFixedAlpha(PixelFormat from, PixelFormat to, byte alpha)
    {
        long bad = 0;
        Parallel.For(0, 256 / Step, new ParallelOptions { MaxDegreeOfParallelism = 8 }, slab =>
        {
            int r = slab * Step;
            var src = new byte[65536 * 4];
            for (int i = 0; i < 65536; i++)
            {
                // Premultiplied color never exceeds alpha, but the conversion must still agree for any input, so use the full range.
                src[i * 4] = (byte)r;
                src[(i * 4) + 1] = (byte)(i >> 8);
                src[(i * 4) + 2] = (byte)i;
                src[(i * 4) + 3] = alpha;
            }

            long local = Compare(src, from, to, 65536, to.BytesPerPixel, r);
            if (local != 0)
            {
                Interlocked.Add(ref bad, local);
            }
        });
        return bad;
    }

    // Converts the slab in rows of varying width through the production entry point and through the float reference.
    private static long Compare(byte[] src, PixelFormat from, PixelFormat to, int pixels, int dbpp, int seed)
    {
        int sbpp = from.BytesPerPixel;
        var actual = new byte[pixels * dbpp];
        var expected = new byte[pixels * dbpp];
        int pos = 0;
        int k = seed;
        while (pos < pixels)
        {
            int w = Math.Min(s_widths[k++ % s_widths.Length], pixels - pos);
            ConvertKernel.ConvertRow(src.AsSpan(pos * sbpp, w * sbpp), from, actual.AsSpan(pos * dbpp, w * dbpp), to, w);
            ConvertKernel.ConvertViaFloat(src.AsSpan(pos * sbpp, w * sbpp), from, expected.AsSpan(pos * dbpp, w * dbpp), to, w);
            pos += w;
        }

        long bad = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                bad++;
            }
        }

        return bad;
    }
}
