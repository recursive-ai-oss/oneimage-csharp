using OneImage.Tests.DitherTests;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Normalization;
using Rgba32Is = SixLabors.ImageSharp.PixelFormats.Rgba32;
using IS = SixLabors.ImageSharp;

namespace OneImage.Tests.HistogramTests;

/// <summary>Auto-level and adaptive threshold against ImageSharp 3.1.12.</summary>
public class HistogramParityTests
{
    private const int W = 203;
    private const int H = 157;

    internal static (int Diff, int Max, double Mean) Compare(Rgba32[] mine, IS.Image<Rgba32Is> theirs, int w, int h)
    {
        int diff = 0, max = 0;
        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32Is t = theirs[x, y];
                Rgba32 m = mine[(y * w) + x];
                int d = Math.Max(Math.Max(Math.Abs(t.R - m.R), Math.Abs(t.G - m.G)), Math.Max(Math.Abs(t.B - m.B), Math.Abs(t.A - m.A)));
                if (d != 0) diff++;
                max = Math.Max(max, d);
                sum += d;
            }
        }

        return (diff, max, sum / (double)(w * h));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutoLevelMatches(bool sync)
    {
        var (px, theirs) = DitherParityTests.Pair(W, H);
        // A photo with a limited range, so there is something to stretch.
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)(40 + (px[i].R * 150 / 255)), (byte)(20 + (px[i].G * 180 / 255)), (byte)(60 + (px[i].B * 120 / 255)), 255);
        }

        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Rgba32 p = px[(y * W) + x];
                theirs[x, y] = new Rgba32Is(p.R, p.G, p.B, 255);
            }

        theirs.Mutate(x => x.HistogramEqualization(new HistogramEqualizationOptions { Method = HistogramEqualizationMethod.AutoLevel, SyncChannels = sync }));
        Rgba32[] mine = Image.FromPixels<Rgba32>(px, W, H).AutoLevel(sync).ToArray<Rgba32>();
        var (diff, max, mean) = Compare(mine, theirs, W, H);
        Assert.True(max <= (sync ? 2 : 1), $"autolevel sync={sync}: {diff} pixels differ, max {max}, mean {mean:F4}");
    }

    [Fact]
    public void AdaptiveThresholdGrayMatches()
    {
        var (px, theirs) = DitherParityTests.Pair(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Rgba32 p = px[(y * W) + x];
                byte g = (byte)((p.R + p.G + p.B) / 3);
                px[(y * W) + x] = new Rgba32(g, g, g, 255);
                theirs[x, y] = new Rgba32Is(g, g, g, 255);
            }

        theirs.Mutate(x => x.AdaptiveThreshold());
        Rgba32[] mine = Image.FromPixels<Rgba32>(px, W, H).AdaptiveThreshold().ToArray<Rgba32>();
        var (diff, _, _) = Compare(mine, theirs, W, H);
        Assert.True(diff == 0, $"gray adaptive threshold: {diff} of {W * H} differ");
    }

    [Theory]
    [InlineData(0.85f, false)]
    [InlineData(0.6f, false)]
    [InlineData(0.85f, true)]
    public void AdaptiveThresholdMatches(float limit, bool colors)
    {
        var (px, theirs) = DitherParityTests.Pair(W, H);
        var upper = colors ? new Rgba32(250, 240, 10, 255) : new Rgba32(255, 255, 255, 255);
        var lower = colors ? new Rgba32(5, 20, 90, 255) : new Rgba32(0, 0, 0, 255);
        theirs.Mutate(x => x.AdaptiveThreshold(new Rgba32Is(upper.R, upper.G, upper.B, upper.A), new Rgba32Is(lower.R, lower.G, lower.B, lower.A), limit));
        Rgba32[] mine = Image.FromPixels<Rgba32>(px, W, H).AdaptiveThreshold(upper, lower, limit).ToArray<Rgba32>();
        var (diff, max, mean) = Compare(mine, theirs, W, H);
        int border = 0, interior = 0;
        string firsts = "";
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (theirs[x, y].R != mine[(y * W) + x].R)
                {
                    if (x < 16 || y < 16 || x >= W - 16 || y >= H - 16) border++; else interior++;
                    if (firsts.Length < 200) firsts += $"({x},{y}) ";
                }

        Assert.True(diff <= 2, $"adaptive threshold {limit}: {diff} of {W * H} differ, border {border}, interior {interior}: {firsts}");
    }
}
