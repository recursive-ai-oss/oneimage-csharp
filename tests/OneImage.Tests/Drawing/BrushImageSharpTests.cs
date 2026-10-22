using System.Numerics;
using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Xunit;
using ColorStop = OneImage.Drawing.ColorStop;
using EllipticGradientBrush = SixLabors.ImageSharp.Drawing.Processing.EllipticGradientBrush;
using ImageBrush = SixLabors.ImageSharp.Drawing.Processing.ImageBrush;
using LinearGradientBrush = SixLabors.ImageSharp.Drawing.Processing.LinearGradientBrush;
using PathGradientBrush = SixLabors.ImageSharp.Drawing.Processing.PathGradientBrush;
using PatternBrush = SixLabors.ImageSharp.Drawing.Processing.PatternBrush;
using RadialGradientBrush = SixLabors.ImageSharp.Drawing.Processing.RadialGradientBrush;
using RecolorBrush = SixLabors.ImageSharp.Drawing.Processing.RecolorBrush;
using Path = OneImage.Drawing.Path;
using Brush = OneImage.Drawing.Brush;
using Sharp = SixLabors.ImageSharp;
using SharpPixel = SixLabors.ImageSharp.PixelFormats.Rgba32;
using SharpStop = SixLabors.ImageSharp.Drawing.Processing.ColorStop;

namespace OneImage.Tests.Drawing;

/// <summary>
/// Agreement of each brush with SixLabors.ImageSharp.Drawing 2.1.7: the same fill is rendered by both on the same backdrop and the
/// RGBA bytes are compared. Mean and maximum difference per case are printed (BRUSH lines).
/// </summary>
public class BrushImageSharpTests
{
    private const int W = 96, H = 64;

    private static readonly Rgba32 Red = new(255, 0, 0, 255);
    private static readonly Rgba32 Green = new(0, 160, 40, 255);
    private static readonly Rgba32 Blue = new(20, 30, 255, 255);
    private static readonly Rgba32 Yellow = new(250, 220, 0, 255);

    private static Sharp.Color C(Rgba32 c) => Sharp.Color.FromPixel(new SharpPixel(c.R, c.G, c.B, c.A));

    // A backdrop that is not flat so that blending errors show.
    private static Rgba32[] Backdrop(bool opaque)
    {
        var px = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                px[(y * W) + x] = opaque
                    ? new Rgba32((byte)(x * 255 / W), (byte)(y * 255 / H), (byte)(((x ^ y) * 7) & 255), 255)
                    : new Rgba32((byte)(255 - (x * 2)), (byte)(40 + y), 90, (byte)(40 + (x * 2)));
            }
        }

        return px;
    }

    private static Rgba32[] Ours(Rgba32[] backdrop, Brush brush, Path? shape = null, float opacity = 1f)
    {
        Image img = Image.FromPixels<Rgba32>(backdrop, W, H);
        return img.Fill(brush, shape ?? Path.Rectangle(0, 0, W, H), antialias: true, opacity: opacity).ToArray<Rgba32>();
    }

    private static Rgba32[] Theirs(Rgba32[] backdrop, Func<Sharp.Image<SharpPixel>, Action<IImageProcessingContext>> fill)
    {
        using var img = new Sharp.Image<SharpPixel>(W, H);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Rgba32 b = backdrop[(y * W) + x];
                img[x, y] = new SharpPixel(b.R, b.G, b.B, b.A);
            }
        }

        img.Mutate(fill(img));
        var result = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                SharpPixel p = img[x, y];
                result[(y * W) + x] = new Rgba32(p.R, p.G, p.B, p.A);
            }
        }

        return result;
    }

    private static (double Mean, int Max, int Over1) Compare(Rgba32[] a, Rgba32[] b)
    {
        long sum = 0;
        int max = 0, over = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int[] d = [Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G), Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A)];
            foreach (int v in d)
            {
                sum += v;
                max = Math.Max(max, v);
                if (v > 1)
                {
                    over++;
                }
            }
        }

        return ((double)sum / (a.Length * 4), max, over);
    }

    // Console output of passing tests is not shown by the runner; setting BRUSH_REPORT to a file path collects the lines there.
    private static void Report(string line)
    {
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("BRUSH_REPORT") is { Length: > 0 } path)
        {
            lock (typeof(BrushImageSharpTests))
            {
                File.AppendAllText(path, line + "\n");
            }
        }
    }

    private static void Agree(string name, Rgba32[] backdrop, Brush ours, Func<Sharp.Image<SharpPixel>, Action<IImageProcessingContext>> theirs, double maxMean, int maxMax, Path? shape = null, float opacity = 1f)
    {
        Rgba32[] o = Ours(backdrop, ours, shape, opacity), t = Theirs(backdrop, theirs);
        (double mean, int max, int over) = Compare(o, t);
        if (mean > maxMean || max > maxMax)
        {
            int shown = 0;
            for (int i = 0; i < o.Length && shown < 8; i += 37)
            {
                if (o[i] != t[i])
                {
                    Console.WriteLine($"  differs at ({i % W},{i / W}): ours {o[i]} theirs {t[i]}");
                    shown++;
                }
            }
        }

        Report($"BRUSH {name}: mean {mean:F4} max {max} channel-values>1 {over}");
        Assert.True(mean <= maxMean, $"{name}: mean {mean} > {maxMean}");
        Assert.True(max <= maxMax, $"{name}: max {max} > {maxMax}");
    }

    private static Func<Sharp.Image<SharpPixel>, Action<IImageProcessingContext>> Whole(SixLabors.ImageSharp.Drawing.Processing.Brush b, float opacity = 1f) =>
        _ => c => c.Fill(new DrawingOptions { GraphicsOptions = new Sharp.GraphicsOptions { BlendPercentage = opacity } }, b);

    private static GradientRepetition Rep(GradientRepetitionMode m) => m switch
    {
        GradientRepetitionMode.Repeat => GradientRepetition.Repeat,
        GradientRepetitionMode.Reflect => GradientRepetition.Reflect,
        GradientRepetitionMode.DontFill => GradientRepetition.DontFill,
        _ => GradientRepetition.None,
    };

    public static TheoryData<GradientRepetitionMode> Modes() =>
        [GradientRepetitionMode.None, GradientRepetitionMode.Repeat, GradientRepetitionMode.Reflect, GradientRepetitionMode.DontFill];

    // Pixels behind the start of the line (position < 0) are left out of these comparisons: ImageSharp.Drawing mirrors them for
    // slanted gradients (position -0.3 looks like 0.3) and clamps them for axis-aligned ones in Repeat and None mode, whereas here
    // None clamps, Repeat continues the pattern backward and DontFill leaves them empty. The start is at the image corner so
    // every pixel has a position of at least 0.
    [Theory]
    [MemberData(nameof(Modes))]
    public void LinearGradientAgrees(GradientRepetitionMode mode)
    {
        foreach (bool opaque in new[] { true, false })
        {
            var stops = new[] { new ColorStop(0f, Red), new ColorStop(0.4f, Yellow), new ColorStop(1f, Blue) };
            Agree($"linear {mode} backdrop-opaque={opaque}", Backdrop(opaque),
                Brush.LinearGradient(new Vector2(0, 0), new Vector2(60, 40), stops, Rep(mode)),
                Whole(new LinearGradientBrush(new Sharp.PointF(0, 0), new Sharp.PointF(60, 40), mode, new SharpStop(0f, C(Red)), new SharpStop(0.4f, C(Yellow)), new SharpStop(1f, C(Blue)))),
                0.2, 2);
        }
    }

    [Fact]
    public void LinearGradientHardStopsAndUnsortedStopsAgree()
    {
        Agree("linear hard stops", Backdrop(true),
            Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), [new(0f, Red), new(0.5f, Red), new(0.5f, Blue), new(1f, Green)]),
            Whole(new LinearGradientBrush(new Sharp.PointF(0, 0), new Sharp.PointF(W, 0), GradientRepetitionMode.None, new SharpStop(0f, C(Red)), new SharpStop(0.5f, C(Red)), new SharpStop(0.5f, C(Blue)), new SharpStop(1f, C(Green)))),
            0.2, 2);
        Agree("linear unsorted stops, stops outside 0..1", Backdrop(true),
            Brush.LinearGradient(new Vector2(0, 0), new Vector2(0, H), [new(1.4f, Blue), new(-0.3f, Yellow), new(0.5f, Red)]),
            Whole(new LinearGradientBrush(new Sharp.PointF(0, 0), new Sharp.PointF(0, H), GradientRepetitionMode.None, new SharpStop(1.4f, C(Blue)), new SharpStop(-0.3f, C(Yellow)), new SharpStop(0.5f, C(Red)))),
            0.2, 2);
    }

    [Fact]
    public void LinearGradientWithTranslucentStopsAgreesWhenInterpolatingStraightColors()
    {
        Rgba32 clear = new(0, 0, 255, 0);
        foreach (bool opaque in new[] { true, false })
        {
            Agree($"linear translucent straight backdrop-opaque={opaque}", Backdrop(opaque),
                Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), [new(0f, Red), new(1f, clear)], premultiplied: false),
                Whole(new LinearGradientBrush(new Sharp.PointF(0, 0), new Sharp.PointF(W, 0), GradientRepetitionMode.None, new SharpStop(0f, C(Red)), new SharpStop(1f, C(clear)))),
                0.3, 2);
        }
    }

    [Fact]
    public void PremultipliedInterpolationKeepsHueWhileFading()
    {
        Rgba32 clear = new(0, 0, 255, 0);
        Rgba32[] px = Ours(new Rgba32[W * H], Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), [new(0f, Red), new(1f, clear)]));
        Rgba32 mid = px[W / 2];
        Assert.InRange(mid.A, 120, 135);
        Assert.True(mid.R >= 250 && mid.B <= 4, $"fading red stays red, got {mid}");
        Rgba32[] straight = Ours(new Rgba32[W * H], Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), [new(0f, Red), new(1f, clear)], premultiplied: false));
        Assert.InRange(straight[W / 2].B, 120, 135);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void RadialGradientAgrees(GradientRepetitionMode mode)
    {
        Agree($"radial {mode}", Backdrop(true),
            Brush.RadialGradient(new Vector2(40, 30), 20, [new(0f, Yellow), new(0.6f, Red), new(1f, Blue)], Rep(mode)),
            Whole(new RadialGradientBrush(new Sharp.PointF(40, 30), 20, mode, new SharpStop(0f, C(Yellow)), new SharpStop(0.6f, C(Red)), new SharpStop(1f, C(Blue)))),
            0.2, 2);
    }

    // ImageSharp.Drawing 2.1.7 turns the ellipse the opposite way for a slanted axis (an axis pointing down-right draws an ellipse
    // elongated up-right); axis-aligned ellipses are unaffected. This library points the axis at the axis end, so the same picture
    // needs the axis end mirrored vertically about the center.
    [Theory]
    [MemberData(nameof(Modes))]
    public void EllipticGradientAgreesWithSquaredDistance(GradientRepetitionMode mode)
    {
        Agree($"elliptic(squared) {mode}", Backdrop(true),
            Brush.EllipticGradient(new Vector2(48, 32), new Vector2(80, 14), 0.45f, [new(0f, Yellow), new(1f, Blue)], Rep(mode), squaredDistance: true),
            Whole(new EllipticGradientBrush(new Sharp.PointF(48, 32), new Sharp.PointF(80, 50), 0.45f, mode, new SharpStop(0f, C(Yellow)), new SharpStop(1f, C(Blue)))),
            0.3, 3);
    }

    [Fact]
    public void PathGradientAgreesInsideThePolygon()
    {
        // ImageSharp evaluates a path gradient at pixel corners (x, y); this library at pixel centers, so the same picture needs
        // the polygon moved by half a pixel. Pixels exactly on the outline are inside for ImageSharp and may differ by a pixel.
        Sharp.PointF[] theirPts = [new(10, 6), new(60, 4), new(88, 30), new(70, 58), new(20, 56), new(4, 30)];
        Vector2[] ours = theirPts.Select(p => new Vector2(p.X + 0.5f, p.Y + 0.5f)).ToArray();
        Rgba32[] colors = [Red, Green, Blue, Yellow, new(255, 255, 255, 255), new(0, 0, 0, 255)];
        Sharp.Color[] theirColors = colors.Select(C).ToArray();
        Rgba32[] back = Backdrop(true);
        Rgba32[] o = Ours(back, Brush.PathGradient(ours, colors, new Rgba32(128, 128, 128, 255)));
        Rgba32[] t = Theirs(back, _ => c => c.Fill(new PathGradientBrush(theirPts, theirColors, C(new Rgba32(128, 128, 128, 255)))));
        (double mean, int max, int over) = Compare(o, t);
        Report($"BRUSH path-gradient hexagon: mean {mean:F4} max {max} channel-values>1 {over}");
        Assert.True(mean <= 0.8, $"mean {mean}");

        // Away from the outline the two agree to rounding.
        int far = 0, bad = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (EdgeDistance(theirPts, x, y) < 1.5)
                {
                    continue;
                }

                far++;
                Rgba32 a = o[(y * W) + x], b = t[(y * W) + x];
                if (Math.Abs(a.R - b.R) > 2 || Math.Abs(a.G - b.G) > 2 || Math.Abs(a.B - b.B) > 2 || Math.Abs(a.A - b.A) > 2)
                {
                    bad++;
                }
            }
        }

        Report($"BRUSH path-gradient hexagon away from outline: {bad} of {far} pixels differ by more than 2");
        Assert.Equal(0, bad);
    }

    [Fact]
    public void PathGradientWithDefaultCenterAndConcavePolygonAgrees()
    {
        Sharp.PointF[] theirPts = [new(6, 4), new(50, 4), new(50, 30), new(88, 30), new(88, 58), new(6, 58)];
        Vector2[] ours = theirPts.Select(p => new Vector2(p.X + 0.5f, p.Y + 0.5f)).ToArray();
        Rgba32[] colors = [Red, Green, Blue, Yellow, new(0, 220, 220, 255), new(220, 0, 220, 255)];
        Rgba32[] back = Backdrop(true);
        Rgba32[] o = Ours(back, Brush.PathGradient(ours, colors));
        Rgba32[] t = Theirs(back, _ => c => c.Fill(new PathGradientBrush(theirPts, colors.Select(C).ToArray())));
        (double mean, int max, int over) = Compare(o, t);
        Report($"BRUSH path-gradient L-shape default center: mean {mean:F4} max {max} channel-values>1 {over}");
        Assert.True(mean <= 1.0, $"mean {mean}");
    }

    public static TheoryData<HatchStyle> Hatches() =>
        [HatchStyle.Horizontal, HatchStyle.Vertical, HatchStyle.ForwardDiagonal, HatchStyle.BackwardDiagonal, HatchStyle.Percent10, HatchStyle.Percent20, HatchStyle.Min];

    [Theory]
    [MemberData(nameof(Hatches))]
    public void HatchPatternsAreExact(HatchStyle style)
    {
        Rgba32 fore = new(250, 240, 10, 255), back = new(10, 20, 120, 255);
        SixLabors.ImageSharp.Drawing.Processing.Brush theirs = style switch
        {
            HatchStyle.Horizontal => Brushes.Horizontal(C(fore), C(back)),
            HatchStyle.Vertical => Brushes.Vertical(C(fore), C(back)),
            HatchStyle.ForwardDiagonal => Brushes.ForwardDiagonal(C(fore), C(back)),
            HatchStyle.BackwardDiagonal => Brushes.BackwardDiagonal(C(fore), C(back)),
            HatchStyle.Percent10 => Brushes.Percent10(C(fore), C(back)),
            HatchStyle.Percent20 => Brushes.Percent20(C(fore), C(back)),
            _ => Brushes.Min(C(fore), C(back)),
        };
        Agree($"hatch {style} whole image", Backdrop(true), Brush.Hatch(style, fore, back), Whole(theirs), 0.0, 0);

        // Inside an integer rectangle (no edge anti-aliasing to differ) the pattern stays anchored at the image origin in both.
        Agree($"hatch {style} in rectangle", Backdrop(true), Brush.Hatch(style, fore, back), _ => c => c.Fill(theirs, new RectangularPolygon(13, 9, 50, 33)), 0.0, 0, Path.Rectangle(13, 9, 50, 33));
    }

    [Fact]
    public void CustomPatternWithTranslucentColorsIsExact()
    {
        var cells = new bool[2, 3];
        cells[0, 0] = true;
        cells[1, 2] = true;
        Rgba32 fore = new(255, 0, 0, 128), back = new(0, 255, 0, 64);
        Agree("pattern custom translucent", Backdrop(true), Brush.Pattern(fore, back, cells),
            Whole(new PatternBrush(C(fore), C(back), cells)), 0.0, 1);
    }

    [Fact]
    public void ImageBrushAgrees()
    {
        var tex = new Rgba32[7 * 5];
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                tex[(y * 7) + x] = new Rgba32((byte)(x * 35), (byte)(y * 50), (byte)((x * y * 9) & 255), (byte)(255 - (x * 10)));
            }
        }

        Image oursTex = Image.FromPixels<Rgba32>(tex, 7, 5);
        using var theirTex = new Sharp.Image<SharpPixel>(7, 5);
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                theirTex[x, y] = new SharpPixel(tex[(y * 7) + x].R, tex[(y * 7) + x].G, tex[(y * 7) + x].B, tex[(y * 7) + x].A);
            }
        }

        Agree("image whole", Backdrop(true), Brush.FromImage(oursTex), Whole(new ImageBrush(theirTex)), 0.0, 1);
        Agree("image region+offset", Backdrop(true), Brush.FromImage(oursTex, new ImageRect(1, 1, 4, 3), offsetX: 2, offsetY: 3),
            Whole(new ImageBrush(theirTex, new Sharp.RectangleF(1, 1, 4, 3), new Sharp.Point(2, 3))), 0.0, 1);

        // ImageSharp anchors the tiling at the top-left of the shape's bounds; here the anchor is the image origin, so the same
        // picture needs the offset set to the shape's corner.
        Agree("image in rectangle (offset = corner)", Backdrop(true), Brush.FromImage(oursTex, offsetX: 13, offsetY: 9),
            _ => c => c.Fill(new ImageBrush(theirTex), new RectangularPolygon(13, 9, 50, 33)), 0.0, 1, Path.Rectangle(13, 9, 50, 33));
        Agree("image whole, opacity .5", Backdrop(true), Brush.FromImage(oursTex), Whole(new ImageBrush(theirTex), 0.5f), 0.05, 1, opacity: 0.5f);
    }

    [Fact]
    public void RecolorBrushAgrees()
    {
        // A backdrop of ramps around the source color so that the whole falloff is exercised.
        var back = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                back[(y * W) + x] = new Rgba32((byte)(100 + (x * 150 / W)), (byte)(80 + (y * 100 / H)), (byte)(60 + ((x + y) * 3 % 120)), 255);
            }
        }

        Rgba32 source = new(150, 110, 90, 255), target = new(20, 230, 60, 255);
        foreach (float threshold in new[] { 0.02f, 0.1f, 0.3f })
        {
            Agree($"recolor threshold {threshold}", back, Brush.Recolor(source, target, threshold),
                Whole(new RecolorBrush(C(source), C(target), threshold)), 0.1, 2);
        }
    }

    private static double EdgeDistance(Sharp.PointF[] p, int x, int y)
    {
        double best = double.MaxValue;
        for (int i = 0; i < p.Length; i++)
        {
            Sharp.PointF a = p[i], b = p[(i + 1) % p.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double t = Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(a.X + (t * dx) - x, 2) + Math.Pow(a.Y + (t * dy) - y, 2)));
        }

        return best;
    }
}
