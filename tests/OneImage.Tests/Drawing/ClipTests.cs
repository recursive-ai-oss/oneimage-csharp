using System.Numerics;
using OneImage.Drawing;
using Xunit;
using IntersectionRule = OneImage.Drawing.IntersectionRule;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests.Drawing;

public class ClipTests
{
    private const int W = 300, H = 260;
    private static readonly Rgba32 Red = new(220, 30, 40, 255);

    private static Rgba32[] Pattern(bool opaque)
    {
        var px = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                px[(y * W) + x] = new Rgba32((byte)(x * 255 / W), (byte)(y * 255 / H), (byte)(((x ^ y) * 3) + (x / 7)), opaque ? (byte)255 : (byte)(60 + ((x + y) % 190)));
            }
        }

        return px;
    }

    private static Image Source(bool opaque = true) => Image.FromPixels<Rgba32>(Pattern(opaque), W, H);

    private static Canvas CanvasOf(bool opaque = true)
    {
        var c = new Canvas(W, H, opaque ? PixelFormat.Rgb8 : PixelFormat.Rgba8);
        if (opaque)
        {
            c.CopyFrom<Rgba32>(Pattern(true), new ImageRect(0, 0, W, H));
        }
        else
        {
            c.CopyFrom<Rgba32>(Pattern(false), new ImageRect(0, 0, W, H));
        }

        return c;
    }

    private static int MaxDiff(Rgba32 a, Rgba32 b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void OutsideIsUntouchedInsideIsTheResultAndTheEdgeBlends(bool opaque, bool blur)
    {
        Image img = Source(opaque);
        const float cx = 140.3f, cy = 120.6f, r = 85.4f;
        Func<Image, Image> ops = blur ? i => i.GaussianBlur(5) : i => i.Invert();
        Rgba32[] orig = img.ToArray<Rgba32>();
        Rgba32[] proc = ops(img).ToArray<Rgba32>();
        Rgba32[] got = img.Clip(Path.Circle(cx, cy, r), ops).ToArray<Rgba32>();
        int outside = 0, inside = 0, edge = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double d = Math.Sqrt(Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2)) - r;
                Rgba32 o = orig[(y * W) + x], p = proc[(y * W) + x], g = got[(y * W) + x];
                if (d > 1.5)
                {
                    Assert.Equal(o, g);
                    outside++;
                }
                else if (d < -1.5)
                {
                    Assert.Equal(p, g);
                    inside++;
                }
                else
                {
                    edge++;
                    foreach ((int a, int b, int c) in new[] { (o.R, p.R, g.R), (o.G, p.G, g.G), (o.B, p.B, g.B), (o.A, p.A, g.A) })
                    {
                        // Premultiplication makes the mix of translucent pixels nonlinear in the straight values, so allow for it there.
                        int slack = opaque ? 1 : 40;
                        Assert.InRange(c, Math.Min(a, b) - slack, Math.Max(a, b) + slack);
                    }
                }
            }
        }

        Assert.True(outside > 1000 && inside > 1000 && edge > 100);
    }

    [Fact]
    public void FractionalEdgeBlendsByCoverage()
    {
        Image img = Source();
        // The rectangle covers 0.25 of column 100 and all of the columns to its right up to 199.
        Rgba32[] orig = img.ToArray<Rgba32>();
        Rgba32[] proc = img.Invert().ToArray<Rgba32>();
        Rgba32[] got = img.Clip(Path.Rectangle(100.75f, 40, 99.25f + 0.5f, 100), i => i.Invert()).ToArray<Rgba32>();
        int y = 60;
        Rgba32 o = orig[(y * W) + 100], p = proc[(y * W) + 100], g = got[(y * W) + 100];
        Assert.InRange(g.R, Math.Round(o.R + ((p.R - o.R) * 0.25)) - 1, Math.Round(o.R + ((p.R - o.R) * 0.25)) + 1);
        Assert.InRange(g.G, Math.Round(o.G + ((p.G - o.G) * 0.25)) - 1, Math.Round(o.G + ((p.G - o.G) * 0.25)) + 1);
        Assert.Equal(proc[(y * W) + 150], got[(y * W) + 150]);
        Assert.Equal(orig[(y * W) + 99], got[(y * W) + 99]);
        // Right edge at 200.5: half of column 200.
        o = orig[(y * W) + 200];
        p = proc[(y * W) + 200];
        g = got[(y * W) + 200];
        Assert.InRange(g.B, Math.Round(o.B + ((p.B - o.B) * 0.5)) - 1, Math.Round(o.B + ((p.B - o.B) * 0.5)) + 1);
    }

    [Fact]
    public void AliasedClipPicksWholePixels()
    {
        Image img = Source();
        Rgba32[] orig = img.ToArray<Rgba32>();
        Rgba32[] proc = img.Invert().ToArray<Rgba32>();
        Rgba32[] got = img.Clip(Path.Star(150, 130, 7, 40, 120), i => i.Invert(), antialias: false).ToArray<Rgba32>();
        int fromProc = 0;
        for (int i = 0; i < got.Length; i++)
        {
            Assert.True(got[i] == orig[i] || got[i] == proc[i]);
            if (got[i] != orig[i])
            {
                fromProc++;
            }
        }

        Assert.True(fromProc > 3000);
    }

    [Fact]
    public void EmptyOffscreenAndEverythingPaths()
    {
        Image img = Source();
        Rgba32[] orig = img.ToArray<Rgba32>();
        Rgba32[] proc = img.Invert().ToArray<Rgba32>();
        Assert.Equal(orig, img.Clip(Path.Empty, i => i.Invert()).ToArray<Rgba32>());
        Assert.Equal(orig, img.Clip(Path.Circle(-500, -500, 50), i => i.Invert()).ToArray<Rgba32>());
        Assert.Equal(proc, img.Clip(Path.Rectangle(-10, -10, W + 20, H + 20), i => i.Invert()).ToArray<Rgba32>());
        // Even-odd: the inner square is a hole.
        Path ring = Path.Combine(Path.Rectangle(-10, -10, W + 20, H + 20), Path.Rectangle(100, 100, 50, 50));
        Rgba32[] eo = img.Clip(ring, i => i.Invert(), rule: IntersectionRule.EvenOdd).ToArray<Rgba32>();
        Assert.Equal(orig[(120 * W) + 120], eo[(120 * W) + 120]);
        Assert.Equal(proc[(20 * W) + 20], eo[(20 * W) + 20]);
    }

    [Fact]
    public void ClipNestsAndTransforms()
    {
        Image img = Source();
        Path circle = Path.Circle(0, 0, 60);
        Rgba32[] a = img.Clip(circle, i => i.Invert(), transform: Matrix3x2.CreateTranslation(150, 130)).ToArray<Rgba32>();
        Rgba32[] b = img.Clip(circle.Translate(150, 130), i => i.Invert()).ToArray<Rgba32>();
        Assert.Equal(b, a);
        // A clip inside a clip: invert in a big circle, then blur only in a small one.
        Rgba32[] nested = img.Clip(Path.Circle(150, 130, 100), i => i.Invert().Clip(Path.Circle(150, 130, 40), j => j.GaussianBlur(3))).ToArray<Rgba32>();
        Rgba32[] inner = img.Invert().Clip(Path.Circle(150, 130, 40), j => j.GaussianBlur(3)).ToArray<Rgba32>();
        Assert.Equal(inner[(130 * W) + 150], nested[(130 * W) + 150]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanvasMatchesImageAtAnyDegree(bool opaque)
    {
        Path path = Path.Combine(Path.Circle(140, 120, 90), Path.Star(220, 190, 5, 15, 55));
        Func<Image, Image> ops = i => i.GaussianBlur(6).Invert();
        using Canvas c = CanvasOf(opaque);
        Image snapshot = c.Snapshot();
        Rgba32[] expected = snapshot.Clip(path, ops).ToArray<Rgba32>(1);
        Rgba32[] eight = snapshot.Clip(path, ops).ToArray<Rgba32>(8);
        Assert.Equal(expected, eight);
        c.Clip(path, ops);
        Assert.Equal(expected, c.Snapshot().ToArray<Rgba32>());
        // The snapshot taken before still shows the old pixels.
        Assert.Equal(Pattern(opaque)[(5 * W) + 5], snapshot.ToArray<Rgba32>()[(5 * W) + 5]);
    }

    [Fact]
    public void BadOperationsAreRefused()
    {
        Image img = Source();
        Path p = Path.Circle(50, 50, 20);
        Assert.Throws<ArgumentNullException>(() => img.Clip(null!, i => i));
        Assert.Throws<ArgumentNullException>(() => img.Clip(p, null!));
        Assert.Throws<ImageOperationException>(() => img.Clip(p, i => i.Resize(width: 100)));
        Assert.Throws<ImageOperationException>(() => img.Clip(p, _ => null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Clip(p, i => i, rule: (IntersectionRule)5));
    }

    [Fact]
    public void ClippedFillAndStroke()
    {
        Image img = Source();
        Path clip = Path.Circle(150, 130, 70);
        Rgba32[] orig = img.ToArray<Rgba32>();
        Path big = Path.Rectangle(0, 0, W, H);
        Rgba32[] filled = img.Fill(Red, big, clip: clip).ToArray<Rgba32>();
        Rgba32[] stroked = img.Draw(Red, 12, Path.Rectangle(20, 20, 260, 220), clip: clip).ToArray<Rgba32>();
        int redIn = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double d = Math.Sqrt(Math.Pow(x + 0.5 - 150, 2) + Math.Pow(y + 0.5 - 130, 2)) - 70;
                if (d > 1.5)
                {
                    Assert.Equal(orig[(y * W) + x], filled[(y * W) + x]);
                    Assert.Equal(orig[(y * W) + x], stroked[(y * W) + x]);
                }
                else if (d < -1.5)
                {
                    Assert.Equal(Red, filled[(y * W) + x]);
                    redIn++;
                }
            }
        }

        Assert.True(redIn > 10000);
        // Inside the circle, near the stroke's rectangle the stroke shows: at the circle's left edge x = 80 the rectangle edge (x 14..26) is outside the circle, so nothing is drawn.
        Assert.Equal(orig[(130 * W) + 20], stroked[(130 * W) + 20]);
        // A stroke across the middle shows only inside the circle.
        Rgba32[] line = img.Draw(Red, 10, Path.Polyline([new(0, 130), new(300, 130)]), clip: clip).ToArray<Rgba32>();
        Assert.Equal(Red, line[(130 * W) + 150]);
        Assert.Equal(orig[(130 * W) + 20], line[(130 * W) + 20]);
        // Same result as a clip around the same draw, up to rounding.
        Rgba32[] viaClip = img.Clip(clip, i => i.Draw(Red, 10, Path.Polyline([new(0, 130), new(300, 130)]))).ToArray<Rgba32>();
        for (int i = 0; i < line.Length; i++)
        {
            Assert.True(MaxDiff(line[i], viaClip[i]) <= 2, $"pixel {i}: {line[i]} vs {viaClip[i]}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClippedFillOnCanvasMatchesImage(bool linear)
    {
        Path clip = Path.Combine(Path.Circle(150, 130, 70), Path.Rectangle(0, 0, 40, 300));
        Path shape = Path.Star(150, 130, 9, 40, 130);
        Brush brush = Brush.LinearGradient(new Vector2(0, 0), new Vector2(300, 260), [new(0f, Red), new(1f, new Rgba32(10, 200, 90, 160))]);
        using Canvas c = CanvasOf(false);
        Rgba32[] expected = c.Snapshot().Fill(brush, shape, linear: linear, clip: clip, clipRule: IntersectionRule.EvenOdd).ToArray<Rgba32>();
        c.Fill(brush, shape, linear: linear, clip: clip, clipRule: IntersectionRule.EvenOdd);
        Assert.Equal(expected, c.Snapshot().ToArray<Rgba32>());
        Rgba32[] unclipped = Source(false).Fill(brush, shape, linear: linear).ToArray<Rgba32>();
        Assert.NotEqual(unclipped, expected);
    }

    [Fact]
    public void ClippedStrokeOnCanvasMatchesImage()
    {
        Path clip = Path.Circle(150, 130, 70);
        using Canvas c = CanvasOf(true);
        Rgba32[] expected = c.Snapshot().Draw(Pen.Dash(Red, 5), Path.Circle(150, 130, 100), clip: clip).ToArray<Rgba32>();
        c.Draw(Pen.Dash(Red, 5), Path.Circle(150, 130, 100), clip: clip);
        Assert.Equal(expected, c.Snapshot().ToArray<Rgba32>());
    }

    [Fact]
    public void ClipDescriptorsAreNamed()
    {
        Image img = Source().Clip(Path.Circle(50, 50, 20), i => i.Invert());
        Assert.Equal("clip", img.Root.Descriptor.Name);
        Assert.Contains(img.Root.Descriptor.Parameters, p => p.Name == "path");
        Image f = Source().Fill(Red, Path.Circle(50, 50, 20), clip: Path.Rectangle(0, 0, 50, 50));
        Assert.Contains(f.Root.Descriptor.Parameters, p => p.Name == "clip");
    }
}
