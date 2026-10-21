using Xunit;

namespace OneImage.Tests.WebP;

public sealed class WebPDecodeTests
{
    public static IEnumerable<object[]> Sizes() =>
    [
        [1, 1], [2, 3], [15, 17], [16, 16], [33, 31], [64, 48], [130, 70], [257, 129],
    ];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void LosslessMatchesLibwebpExactly(int w, int h)
    {
        foreach (bool alpha in new[] { false, true })
        {
            byte[] src = WebPOracle.MakeImage(w, h, w + h, alpha);
            byte[] file = WebPOracle.SkiaEncode(src, w, h, lossless: true, quality: 100);
            byte[] want = WebPOracle.SkiaDecode(file, out _, out _);
            byte[] got = WebPOracle.OurDecode(file, out int gw, out int gh);
            Assert.Equal((w, h), (gw, gh));
            Assert.Equal(want, got);
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void PaletteLosslessMatchesLibwebpExactly(int w, int h)
    {
        foreach (int colors in new[] { 2, 3, 4, 9, 16, 17, 200 })
        {
            byte[] src = WebPOracle.MakePalette(w, h, colors, colors, alpha: colors % 2 == 0);
            byte[] file = WebPOracle.SkiaEncode(src, w, h, lossless: true, quality: 100);
            byte[] want = WebPOracle.SkiaDecode(file, out _, out _);
            byte[] got = WebPOracle.OurDecode(file, out _, out _);
            Assert.Equal(want, got);
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void LossyMatchesLibwebpWithinOneLevel(int w, int h)
    {
        foreach (int q in new[] { 10, 50, 75, 95 })
        {
            byte[] src = WebPOracle.MakeImage(w, h, q + w, alpha: false);
            byte[] file = WebPOracle.SkiaEncode(src, w, h, lossless: false, quality: q);
            byte[] want = WebPOracle.SkiaDecode(file, out _, out _);
            byte[] got = WebPOracle.OurDecode(file, out _, out _);
            Assert.True(WebPOracle.MaxDiff(want, got) <= 1, $"q={q} {w}x{h} diff {WebPOracle.MaxDiff(want, got)}");
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void LossyWithAlphaMatchesLibwebp(int w, int h)
    {
        byte[] src = WebPOracle.MakeImage(w, h, 3, alpha: true);
        byte[] file = WebPOracle.SkiaEncode(src, w, h, lossless: false, quality: 80);
        byte[] want = WebPOracle.SkiaDecode(file, out _, out _);
        byte[] got = WebPOracle.OurDecode(file, out _, out _);
        Assert.True(WebPOracle.MaxDiff(want, got) <= 1, $"diff {WebPOracle.MaxDiff(want, got)}");
    }
}
