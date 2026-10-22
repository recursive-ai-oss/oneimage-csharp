using System.Globalization;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Normalization;
using Xunit;
using IsImage = SixLabors.ImageSharp.Image;
using IsMethod = SixLabors.ImageSharp.Processing.Processors.Normalization.HistogramEqualizationMethod;
using IsL8 = SixLabors.ImageSharp.PixelFormats.L8;
using IsRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;
using IsRgba64 = SixLabors.ImageSharp.PixelFormats.Rgba64;

namespace OneImage.Tests.Equalize;

/// <summary>
/// Compares <see cref="Image.HistogramEqualization"/> with ImageSharp 3.1.12 on the same pixels. Set ONEIMAGE_EQUALIZE_REPORT to a
/// file path to append the max and mean difference of every case.
/// </summary>
public class EqualizeOracleTests
{
    private static readonly HistogramEqualizationMethod[] s_methods =
    [
        HistogramEqualizationMethod.Global,
        HistogramEqualizationMethod.AdaptiveTileInterpolation,
        HistogramEqualizationMethod.AdaptiveSlidingWindow,
    ];

    internal static Rgba32[] Picture(string kind, int w, int h)
    {
        var px = new Rgba32[w * h];
        var rnd = new Random(w * 31 + h);
        switch (kind)
        {
            case "photo":
                return Gif.GifTestUtil.SyntheticPhoto(w, h, 3);
            case "alpha-photo":
                px = Gif.GifTestUtil.SyntheticPhoto(w, h, 4);
                for (int i = 0; i < px.Length; i++)
                {
                    px[i] = new Rgba32(px[i].R, px[i].G, px[i].B, (byte)(i % 5 == 0 ? 255 : rnd.Next(0, 256)));
                }

                return px;
            case "gradient":
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte v = (byte)(x * 255 / Math.Max(1, w - 1));
                        px[(y * w) + x] = new Rgba32(v, (byte)(255 - v), (byte)(y * 255 / Math.Max(1, h - 1)), 255);
                    }
                }

                return px;
            case "noise":
                for (int i = 0; i < px.Length; i++)
                {
                    px[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
                }

                return px;
            case "near-constant":
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte v = (byte)(100 + rnd.Next(0, 2));
                        if (x > w / 2 && y > h / 2 && x < w - 5)
                        {
                            v = (byte)(180 + rnd.Next(0, 3));
                        }

                        px[(y * w) + x] = new Rgba32(v, v, v, 255);
                    }
                }

                return px;
            default:
                throw new ArgumentException(kind);
        }
    }

    private static (int Max, double Mean) Diff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            sum += d;
        }

        return (max, sum / (double)a.Length);
    }

    private static HistogramEqualizationOptions Options(HistogramEqualizationMethod m, int levels, bool clip, int limit, int tiles) => new()
    {
        Method = m switch
        {
            HistogramEqualizationMethod.Global => IsMethod.Global,
            HistogramEqualizationMethod.AdaptiveTileInterpolation => IsMethod.AdaptiveTileInterpolation,
            _ => IsMethod.AdaptiveSlidingWindow,
        },
        LuminanceLevels = levels,
        ClipHistogram = clip,
        ClipLimit = limit,
        NumberOfTiles = tiles,
    };

    private static void Report(string name, (int Max, double Mean) d)
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_EQUALIZE_REPORT");
        if (path != null)
        {
            File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture, $"{name}: max {d.Max} mean {d.Mean:F4}\n"));
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        var sizes = new (string Kind, int W, int H)[]
        {
            ("photo", 321, 241),
            ("alpha-photo", 160, 120),
            ("gradient", 200, 130),
            ("noise", 97, 75),
            ("near-constant", 150, 110),
            ("photo", 211, 157),
            ("photo", 37, 29),
            ("photo", 64, 64),
        };
        foreach ((string kind, int w, int h) in sizes)
        {
            foreach (HistogramEqualizationMethod m in s_methods)
            {
                yield return new object[] { kind, w, h, (int)m, false, 350, 8 };
                yield return new object[] { kind, w, h, (int)m, true, m == HistogramEqualizationMethod.Global ? 40 : 20, 8 };
            }
        }

        yield return new object[] { "photo", 321, 241, (int)HistogramEqualizationMethod.AdaptiveTileInterpolation, true, 350, 4 };
        yield return new object[] { "photo", 321, 241, (int)HistogramEqualizationMethod.AdaptiveSlidingWindow, true, 350, 3 };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RgbaMatchesImageSharp(string kind, int w, int h, int methodValue, bool clip, int limit, int tiles)
    {
        var method = (HistogramEqualizationMethod)methodValue;
        Rgba32[] src = Picture(kind, w, h);
        Rgba32[] ours = Image.FromPixels<Rgba32>(src, w, h).HistogramEqualization(method, 256, clip, limit, tiles).ToArray<Rgba32>();
        using SixLabors.ImageSharp.Image<IsRgba32> img = IsImage.LoadPixelData<IsRgba32>(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(src), w, h);
        img.Mutate(x => x.HistogramEqualization(Options(method, 256, clip, limit, tiles)));
        byte[] theirs = new byte[w * h * 4];
        img.CopyPixelDataTo(theirs);
        (int Max, double Mean) d = Diff(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(ours), theirs);
        string name = $"{kind} {w}x{h} {method} clip={clip}/{limit} tiles={tiles} rgba8";
        Report(name, d);
        Assert.True(d.Max <= 2, $"{name}: max {d.Max} mean {d.Mean:F4}");
    }

    [Theory]
    [InlineData(HistogramEqualizationMethod.Global, false)]
    [InlineData(HistogramEqualizationMethod.AdaptiveTileInterpolation, true)]
    [InlineData(HistogramEqualizationMethod.AdaptiveSlidingWindow, true)]
    public void GrayMatchesImageSharp(HistogramEqualizationMethod method, bool clip)
    {
        const int W = 173, H = 131;
        Rgba32[] src = Picture("photo", W, H);
        var gray = new Gray8[W * H];
        var gbytes = new byte[W * H];
        for (int i = 0; i < gray.Length; i++)
        {
            gbytes[i] = (byte)((src[i].R + src[i].G) / 2);
            gray[i] = new Gray8(gbytes[i]);
        }

        Gray8[] ours = Image.FromPixels<Gray8>(gray, W, H).HistogramEqualization(method, 256, clip, 20, 8).ToArray<Gray8>();
        using SixLabors.ImageSharp.Image<IsL8> img = IsImage.LoadPixelData<IsL8>(gbytes, W, H);
        img.Mutate(x => x.HistogramEqualization(Options(method, 256, clip, 20, 8)));
        byte[] theirs = new byte[W * H];
        img.CopyPixelDataTo(theirs);
        (int Max, double Mean) d = Diff(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Gray8>(ours), theirs);
        Report($"gray8 {W}x{H} {method} clip={clip}", d);
        Assert.True(d.Max <= 2, $"max {d.Max} mean {d.Mean:F4}");
    }

    [Theory]
    [InlineData(HistogramEqualizationMethod.Global, false, 65536)]
    [InlineData(HistogramEqualizationMethod.AdaptiveTileInterpolation, false, 65536)]
    [InlineData(HistogramEqualizationMethod.AdaptiveSlidingWindow, false, 65536)]
    [InlineData(HistogramEqualizationMethod.Global, false, 256)]
    public void SixteenBitMatchesImageSharp(HistogramEqualizationMethod method, bool clip, int levels)
    {
        const int W = 120, H = 90;
        Rgba32[] src = Picture("photo", W, H);
        var rnd = new Random(5);
        var px = new Rgba64[W * H];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba64((ushort)((src[i].R * 257) + rnd.Next(0, 200)), (ushort)((src[i].G * 257) + rnd.Next(0, 200)), (ushort)((src[i].B * 257) + rnd.Next(0, 200)), 65535);
        }

        Rgba64[] ours = Image.FromPixels<Rgba64>(px, W, H).HistogramEqualization(method, levels, clip, 20, 8).ToArray<Rgba64>();
        using SixLabors.ImageSharp.Image<IsRgba64> img = IsImage.LoadPixelData<IsRgba64>(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba64>(px), W, H);
        img.Mutate(x => x.HistogramEqualization(Options(method, levels, clip, 20, 8)));
        byte[] theirs = new byte[W * H * 8];
        img.CopyPixelDataTo(theirs);
        ReadOnlySpan<ushort> a = System.Runtime.InteropServices.MemoryMarshal.Cast<Rgba64, ushort>(ours);
        ReadOnlySpan<ushort> b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(theirs);
        int max = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            sum += d;
        }

        Report($"rgba64 {W}x{H} {method} clip={clip} levels={levels} (16-bit units)", (max, sum / (double)a.Length));
        // 2 levels at 8 bits is 514 at 16 bits.
        Assert.True(max <= 514, $"max {max} mean {sum / (double)a.Length:F2}");
    }
}
