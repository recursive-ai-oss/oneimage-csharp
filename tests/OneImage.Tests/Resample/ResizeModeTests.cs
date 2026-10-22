using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Is = SixLabors.ImageSharp;
using IsPx = SixLabors.ImageSharp.PixelFormats;

namespace OneImage.Tests.Resample;

public class ResizeModeTests
{
    private static readonly Rgba32 s_red = new(255, 0, 0, 255);
    private static readonly Rgba32 s_blue = new(0, 0, 255, 255);

    private static (int W, int H, int X0, int Y0, int X1, int Y1) Describe(int w, int h, Func<int, int, Rgba32> at)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32 p = at(x, y);
                if (p.R > 128 && p.B < 128)
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        return (w, h, x0, y0, x1, y1);
    }

    private static Image Red(int w, int h) => Image.FromPixels<Rgba32>(Enumerable.Repeat(s_red, w * h).ToArray(), w, h);

    private static (int, int, int, int, int, int) Ours(int sw, int sh, int? w, int? h, Fit fit)
    {
        Image r = Red(sw, sh).Resize(w, h, fit, filter: ResamplingFilter.Bilinear, linear: false, background: s_blue);
        Rgba32[] px = r.ToArray<Rgba32>();
        return Describe(r.Width, r.Height, (x, y) => px[(y * r.Width) + x]);
    }

    private static (int, int, int, int, int, int) Theirs(int sw, int sh, int w, int h, ResizeMode mode)
    {
        using var img = new Is.Image<IsPx.Rgba32>(sw, sh, new IsPx.Rgba32(255, 0, 0, 255));
        img.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(w, h), Mode = mode, PadColor = Is.Color.Blue, Sampler = KnownResamplers.Triangle }));
        return Describe(img.Width, img.Height, (x, y) => { IsPx.Rgba32 p = img[x, y]; return new Rgba32(p.R, p.G, p.B, p.A); });
    }

    public static IEnumerable<TheoryDataRow<int, int, int, int, string>> Cases()
    {
        foreach ((int sw, int sh) in new[] { (100, 60), (60, 100), (40, 30), (101, 37) })
        {
            foreach ((int w, int h) in new[] { (50, 50), (200, 150), (80, 0), (0, 30), (30, 80), (20, 20), (150, 30), (100, 60), (7, 90) })
            {
                foreach (string mode in new[] { "BoxPad", "Min", "Max", "Crop", "Pad", "Stretch" })
                {
                    yield return new(sw, sh, w, h, mode);
                }
            }
        }
    }

    [Fact]
    public void MatchesImageSharpSizeAndPlacement()
    {
        // Sizes and placement agree with ImageSharp. Three small, documented differences remain: a derived side that lands exactly on .5 rounds
        // up here and to even there, an odd leftover when centering goes to the bottom/right here and the top/left there, and Min/Max with
        // only one side given use the given side exactly. So every number is within one pixel, and nearly all are identical.
        int cases = 0;
        int exact = 0;
        foreach (var row in Cases())
        {
            (int sw, int sh, int w, int h, string mode) = row.Data;
            Fit fit = mode switch
            {
                "BoxPad" => Fit.BoxPad,
                "Min" => Fit.Min,
                "Max" => Fit.Max,
                "Crop" => Fit.Crop,
                "Pad" => Fit.Pad,
                _ => Fit.Stretch,
            };
            var t = Theirs(sw, sh, w, h, Enum.Parse<ResizeMode>(mode));
            var o = Ours(sw, sh, w == 0 ? null : w, h == 0 ? null : h, fit);
            int[] a = [t.Item1, t.Item2, t.Item3, t.Item4, t.Item5, t.Item6];
            int[] b = [o.Item1, o.Item2, o.Item3, o.Item4, o.Item5, o.Item6];
            for (int i = 0; i < 6; i++)
            {
                Assert.True(Math.Abs(a[i] - b[i]) <= 1, $"{sw}x{sh} box {w}x{h} {mode}: ImageSharp {string.Join(',', a)} ours {string.Join(',', b)}");
            }

            cases++;
            if (a.SequenceEqual(b))
            {
                exact++;
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{exact} of {cases} resize cases identical to ImageSharp");
        Assert.True(exact * 100 >= cases * 85, $"{exact} of {cases} identical");
    }

    [Fact]
    public void FitAliasesAreTheDocumentedModes()
    {
        Assert.Equal(Fit.Contain, Fit.Max);
        Assert.Equal(Fit.Cover, Fit.Crop);
        Assert.Equal(Fit.Pad, Enum.Parse<Fit>("Pad"));
    }

    [Fact]
    public void BoxPadAndMinNeverEnlargeAndHonorAnchors()
    {
        // 40x30 into 100x100: unscaled, bottom-right.
        Image r = Red(40, 30).Resize(100, 100, Fit.BoxPad, Anchor.BottomRight, upscale: true, background: s_blue);
        Assert.Equal((100, 100), (r.Width, r.Height));
        Rgba32[] px = r.ToArray<Rgba32>();
        Assert.Equal(s_blue, px[(69 * 100) + 59]);
        Assert.Equal(s_red, px[(70 * 100) + 60]);
        Assert.Equal(s_red, px[(99 * 100) + 99]);

        // A box larger than the image on either axis leaves Min at the source size, even with upscale: true.
        Image m = Red(40, 30).Resize(20, 100, Fit.Min);
        Assert.Equal((40, 30), (m.Width, m.Height));
    }

    [Fact]
    public void ManualMatchesImageSharpAndClipsToTheCanvas()
    {
        foreach ((int cw, int ch, int tx, int ty, int tw, int th) in new[] { (80, 80, 10, 20, 50, 30), (80, 80, -10, -5, 120, 70), (80, 80, 60, 60, 50, 50), (30, 30, 0, 0, 200, 200), (50, 40, 0, 0, 50, 40) })
        {
            using var img = new Is.Image<IsPx.Rgba32>(100, 60, new IsPx.Rgba32(255, 0, 0, 255));
            img.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(cw, ch), Mode = ResizeMode.Manual, TargetRectangle = new Rectangle(tx, ty, tw, th), PadColor = Is.Color.Blue, Sampler = KnownResamplers.Triangle }));
            var theirs = Describe(img.Width, img.Height, (x, y) => { IsPx.Rgba32 p = img[x, y]; return new Rgba32(p.R, p.G, p.B, p.A); });

            Image r = Red(100, 60).ResizeInto(cw, ch, new ImageRect(tx, ty, tw, th), ResamplingFilter.Bilinear, linear: false, background: s_blue);
            Rgba32[] px = r.ToArray<Rgba32>();
            var ours = Describe(r.Width, r.Height, (x, y) => px[(y * r.Width) + x]);
            Assert.Equal(theirs, ours);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Red(10, 10).ResizeInto(20, 20, new ImageRect(30, 30, 5, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Red(10, 10).ResizeInto(20, 20, new ImageRect(0, 0, 0, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Red(10, 10).ResizeInto(0, 20, new ImageRect(0, 0, 5, 5)));
    }

    [Fact]
    public void ManualScalesTheWholeImageIntoTheTarget()
    {
        // Left half dark, right half light: the split lands in the middle of the target rectangle.
        var px = new Rgba32[40 * 20];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = (i % 40) < 20 ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255);
        }

        Image r = Image.FromPixels<Rgba32>(px, 40, 20).ResizeInto(100, 60, new ImageRect(10, 20, 80, 20), ResamplingFilter.Nearest);
        Rgba32[] o = r.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 0, 255), o[(25 * 100) + 49]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), o[(25 * 100) + 50]);
        Assert.Equal(default, o[(10 * 100) + 50]);
        Assert.Equal(default, o[(25 * 100) + 9]);
    }
}
