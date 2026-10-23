using System.Security.Cryptography;

namespace OneImage.Tests.Resample;

/// <summary>
/// The case matrix of the resize byte-compare tests: deterministic sources of several pixel formats, the sizes and filters to
/// resize them with, and a short hash of each output. The expected hashes in <c>testdata/resize/reference.txt</c> were captured
/// from the 1.6.0 implementation before the allocation-free path existed.
/// </summary>
internal static class ResizeMatrix
{
    public static readonly string[] Formats = ["Gray8", "GrayAlpha8", "Gray16", "Rgb24", "Rgba32", "Rgba32Premultiplied", "Rgb48", "Rgba64", "RgbaFloat"];

    /// <summary>One resize to run: source format and size, target size, filter, linear light.</summary>
    public readonly record struct Case(string Format, int SourceWidth, int SourceHeight, int Width, int Height, ResamplingFilter Filter, bool Linear)
    {
        public string Key => $"{Format}|{SourceWidth}x{SourceHeight}>{Width}x{Height}|{Filter}|{(Linear ? "lin" : "enc")}";
    }

    // Source size, target size. Covers: the OCR shape, a 1x1 source, tiny odd sizes, 3x upscale, sizes that cross 128-pixel tile
    // edges, single-axis changes, ratios just below and above the box pre-reduce threshold of 4, partial last reduce groups, and
    // one large reduction.
    private static readonly (int Sw, int Sh, int W, int H)[] s_sizes =
    [
        (2300, 60, 1227, 32),
        (1, 1, 1, 1),
        (1, 1, 3, 3),
        (7, 3, 3, 2),
        (7, 3, 21, 9),
        (50, 40, 150, 120),
        (129, 257, 65, 100),
        (129, 257, 200, 300),
        (129, 257, 129, 100),
        (129, 257, 40, 257),
        (130, 130, 33, 33),
        (130, 130, 32, 32),
        (13, 11, 3, 2),
        (17, 19, 2, 2),
        (300, 500, 50, 40),
        (800, 600, 100, 75),
        (1, 1000, 1, 10),
        (1000, 1, 10, 1),
    ];

    private static readonly ResamplingFilter[] s_quickFilters = [ResamplingFilter.Auto, ResamplingFilter.Lanczos3, ResamplingFilter.Nearest, ResamplingFilter.Box, ResamplingFilter.Mitchell];

    private static readonly ResamplingFilter[] s_largeFilters = [ResamplingFilter.Auto, ResamplingFilter.Lanczos3, ResamplingFilter.Box, ResamplingFilter.Nearest, ResamplingFilter.CatmullRom];

    public static IEnumerable<Case> All()
    {
        ResamplingFilter[] every = Enum.GetValues<ResamplingFilter>();
        foreach ((int sw, int sh, int w, int h) in s_sizes)
        {
            foreach (string format in Formats)
            {
                // Every filter on the two formats with dedicated fast paths; a handful of filters on the others.
                ResamplingFilter[] filters = format is "Gray8" or "Rgba32" ? every : s_quickFilters;
                foreach (ResamplingFilter filter in filters)
                {
                    yield return new Case(format, sw, sh, w, h, filter, true);
                    yield return new Case(format, sw, sh, w, h, filter, false);
                }
            }
        }

        foreach (string format in new[] { "Gray8", "Rgb24", "Rgba32" })
        {
            foreach (ResamplingFilter filter in s_largeFilters)
            {
                yield return new Case(format, 3000, 2000, 400, 267, filter, true);
                yield return new Case(format, 3000, 2000, 400, 267, filter, false);
            }
        }
    }

    /// <summary>A source image and its pixels as raw bytes in the same format.</summary>
    public static (Image Image, byte[] Bytes, PixelFormat Format) Source(string format, int w, int h)
    {
        Rgba32[] baseRgba = BaseRgba(w, h);
        switch (format)
        {
            case "Gray8":
                return Typed(Array.ConvertAll(baseRgba, p => new Gray8(Luma(p))), w, h);
            case "GrayAlpha8":
                return Typed(Array.ConvertAll(baseRgba, p => new GrayAlpha8(Luma(p), p.A)), w, h);
            case "Gray16":
                return Typed(Array.ConvertAll(baseRgba, p => new Gray16((ushort)((Luma(p) * 257) ^ (p.B & 15)))), w, h);
            case "Rgb24":
                return Typed(Array.ConvertAll(baseRgba, p => new Rgb24(p.R, p.G, p.B)), w, h);
            case "Rgba32":
                return Typed(baseRgba, w, h);
            case "Rgba32Premultiplied":
                Image pre = Image.FromPixels<Rgba32>(baseRgba, w, h).ConvertTo(PixelFormat.Rgba8Premultiplied);
                return (pre, Read(pre), pre.Format);
            case "Rgb48":
                return Typed(Array.ConvertAll(baseRgba, p => new Rgb48((ushort)((p.R * 257) ^ (p.A & 7)), (ushort)(p.G * 257), (ushort)((p.B * 257) ^ (p.R & 15)))), w, h);
            case "Rgba64":
                return Typed(Array.ConvertAll(baseRgba, p => new Rgba64((ushort)(p.R * 257), (ushort)((p.G * 257) ^ (p.R & 15)), (ushort)(p.B * 257), (ushort)((p.A * 257) ^ (p.B & 3)))), w, h);
            case "RgbaFloat":
                return Typed(Array.ConvertAll(baseRgba, p => new RgbaFloat(p.R / 255f, p.G / 255f, (p.B / 255f) * 0.999f, p.A / 255f)), w, h);
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static byte[] Read(Image image)
    {
        var bytes = new byte[image.Width * image.Height * image.Format.BytesPerPixel];
        Render.ReadRegion(image.Frames[0].Node, new ImageRect(0, 0, image.Width, image.Height), bytes, 0, image.Format, RenderContext.Create(0, default));
        return bytes;
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    private static (Image, byte[], PixelFormat) Typed<T>(T[] pixels, int w, int h)
        where T : unmanaged, IPixel<T> =>
        (Image.FromPixels<T>(pixels, w, h), OneImage.Kernels.SpanCast.AsBytes<T>(pixels).ToArray(), T.Format);

    private static byte Luma(Rgba32 p) => (byte)(((p.R * 3) + (p.G * 5) + (p.B * 8)) >> 4);

    // Smooth gradients plus noise, with transparent, partly transparent and opaque pixels: a stand-in for photographs and scans.
    private static Rgba32[] BaseRgba(int w, int h)
    {
        uint s = 2166136261u ^ (uint)((w * 73856093) ^ (h * 19349663));
        var pixels = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                int n = (int)(s & 31);
                int r = (int)Math.Min(255L, (x * 255L / Math.Max(1, w - 1)) + (n >> 1));
                int g = (int)Math.Min(255L, (y * 255L / Math.Max(1, h - 1)) + ((s >> 8) & 15));
                int b = (((x * 3) + (y * 5)) & 255) ^ (int)((s >> 16) & 31);
                int roll = (int)((s >> 24) & 31);
                int a = roll < 3 ? 0 : roll < 6 ? (int)((s >> 3) & 255) : 255;
                pixels[(y * w) + x] = new Rgba32((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }

        return pixels;
    }
}
