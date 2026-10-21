using System.Buffers.Binary;
using OneImage.Codecs.WebP;
using Xunit;

namespace OneImage.Tests.WebP;

public sealed class WebPLosslessEncodeTests
{
    internal static byte[] Wrap(byte[] vp8l)
    {
        byte[] file = new byte[12 + 8 + vp8l.Length + (vp8l.Length & 1)];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        "WEBPVP8L"u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)vp8l.Length);
        vp8l.CopyTo(file.AsSpan(20));
        return file;
    }

    internal static uint[] ToArgb(byte[] rgba)
    {
        uint[] a = new uint[rgba.Length / 4];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = ((uint)rgba[(i * 4) + 3] << 24) | ((uint)rgba[i * 4] << 16) | ((uint)rgba[(i * 4) + 1] << 8) | rgba[(i * 4) + 2];
        }

        return a;
    }

    private static VP8LEncodeOptions Make(string name) => name switch
    {
        "plain" => new VP8LEncodeOptions { SubtractGreen = false, Predictor = false, CrossColor = false, CacheBits = 0, Lz77 = false, Cluster = false, Palette = false },
        "lz77" => new VP8LEncodeOptions { SubtractGreen = false, Predictor = false, CrossColor = false, CacheBits = 0, Cluster = false, Palette = false },
        "subgreen" => new VP8LEncodeOptions { SubtractGreen = true, Predictor = false, CrossColor = false, CacheBits = 0, Cluster = false, Palette = false },
        "predictor" => new VP8LEncodeOptions { SubtractGreen = false, Predictor = true, CrossColor = false, CacheBits = 0, Cluster = false, Palette = false },
        "predictor2" => new VP8LEncodeOptions { SubtractGreen = false, Predictor = true, CrossColor = false, CacheBits = 0, Cluster = false, Palette = false, TransformBits = 2 },
        "cross" => new VP8LEncodeOptions { SubtractGreen = false, Predictor = false, CrossColor = true, CacheBits = 0, Cluster = false, Palette = false },
        "all" => new VP8LEncodeOptions { SubtractGreen = true, Predictor = true, CrossColor = true, CacheBits = 0, Cluster = false, Palette = false },
        "cache1" => new VP8LEncodeOptions { CacheBits = 1, Cluster = false, Palette = false },
        "cache6" => new VP8LEncodeOptions { CacheBits = 6, Cluster = false, Palette = false },
        "cache11" => new VP8LEncodeOptions { CacheBits = 11, Cluster = false, Palette = false },
        "default" => new VP8LEncodeOptions(),
        "effort0" => new VP8LEncodeOptions { Effort = 0 },
        "effort6" => new VP8LEncodeOptions { Effort = 6 },
        "clusters" => new VP8LEncodeOptions { HistogramBits = 3 },
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Configs() =>
    [
        ["plain"],
        ["lz77"],
        ["subgreen"],
        ["predictor"],
        ["predictor2"],
        ["cross"],
        ["all"],
        ["cache1"],
        ["cache6"],
        ["cache11"],
        ["default"],
        ["effort0"],
        ["effort6"],
        ["clusters"],
    ];

    [Theory]
    [MemberData(nameof(Configs))]
    public void RoundTripsPhotoLike(string name)
    {
        VP8LEncodeOptions opts = Make(name);
        foreach ((int w, int h) in new[] { (1, 1), (3, 5), (17, 9), (64, 64), (200, 131) })
        {
            foreach (bool alpha in new[] { false, true })
            {
                byte[] src = WebPOracle.MakeImage(w, h, w * h, alpha);
                CheckRoundTrip(src, w, h, opts);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void RoundTripsPaletteImages(string name)
    {
        VP8LEncodeOptions opts = Make(name);
        foreach (int colors in new[] { 1, 2, 3, 4, 5, 16, 17, 100, 256 })
        {
            foreach ((int w, int h) in new[] { (1, 7), (13, 11), (150, 90) })
            {
                byte[] src = WebPOracle.MakePalette(w, h, colors, colors + w, alpha: colors % 2 == 1);
                CheckRoundTrip(src, w, h, opts);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(255)]
    public void PalettePathIsUsedAndExact(int colors)
    {
        byte[] src = WebPOracle.MakePalette(120, 80, colors, 9);
        CheckRoundTrip(src, 120, 80, new VP8LEncodeOptions { Palette = true });
    }

    internal static void CheckRoundTrip(byte[] src, int w, int h, VP8LEncodeOptions opts)
    {
        bool hasAlpha = false;
        for (int i = 3; i < src.Length; i += 4)
        {
            hasAlpha |= src[i] != 255;
        }

        byte[] vp8l = VP8LEncoder.Encode(ToArgb(src), w, h, hasAlpha, opts, CancellationToken.None);
        byte[] file = Wrap(vp8l);
        byte[] ours = WebPOracle.OurDecode(file, out int gw, out int gh);
        Assert.Equal((w, h), (gw, gh));
        Assert.Equal(src, ours);
        byte[] theirs = WebPOracle.SkiaDecode(file, out _, out _);
        Assert.Equal(src, theirs);
    }
}
