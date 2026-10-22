using System.Numerics;
using OneImage.Drawing;
using Xunit;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests.Drawing;

/// <summary>Behavior of the gradient, pattern, image and recolor brushes: determinism, Image/Canvas equality, linear light, hostile input.</summary>
public class BrushTests
{
    private const int W = 300, H = 260;

    private static readonly Rgba32 Red = new(255, 0, 0, 255);
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);
    private static readonly Rgba32 Yellow = new(250, 220, 0, 255);

    private static Path Scene() => Path.Combine(
        Path.Circle(150, 120, 90),
        Path.Star(200, 150, 6, 30, 80, 10),
        Path.Rectangle(100.4f, 20.3f, 160.25f, 33.6f),
        Path.ParseSvg("M10 200 C 60 120 120 300 290 230 L 270 250 Q 150 280 40 245 Z"));

    private static Rgba32[] Backdrop(int w, int h, bool opaque)
    {
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = new Rgba32((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x ^ y) * 3), opaque ? (byte)255 : (byte)(40 + ((x + y) % 200)));
            }
        }

        return px;
    }

    private static Image Texture()
    {
        var px = new Rgba32[37 * 23];
        for (int y = 0; y < 23; y++)
        {
            for (int x = 0; x < 37; x++)
            {
                px[(y * 37) + x] = new Rgba32((byte)(x * 7), (byte)(y * 11), (byte)((x * y) & 255), (byte)(255 - (x * 3)));
            }
        }

        return Image.FromPixels<Rgba32>(px, 37, 23);
    }

    public static TheoryData<string> Names() =>
        ["linear", "linear-repeat", "linear-translucent", "radial-reflect", "elliptic", "path", "pattern", "hatch", "pattern-scaled", "image", "image-rotated", "recolor"];

    private static Brush Make(string name) => name switch
    {
        "linear" => Brush.LinearGradient(new Vector2(30, 20), new Vector2(250, 210), [new(0f, Red), new(0.5f, Yellow), new(1f, Blue)]),
        "linear-repeat" => Brush.LinearGradient(new Vector2(0, 0), new Vector2(70, 30), [new(0f, Red), new(1f, Blue)], GradientRepetition.Repeat),
        "linear-translucent" => Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), [new(0f, new Rgba32(255, 0, 0, 255)), new(1f, new Rgba32(0, 0, 255, 0))]),
        "radial-reflect" => Brush.RadialGradient(new Vector2(150, 130), 40, [new(0f, Yellow), new(1f, Blue)], GradientRepetition.Reflect),
        "elliptic" => Brush.EllipticGradient(new Vector2(150, 130), new Vector2(230, 170), 0.4f, [new(0f, Yellow), new(1f, Blue)]),
        "path" => Brush.PathGradient([new(20, 20), new(280, 40), new(250, 240), new(30, 220)], [Red, Yellow, Blue, new Rgba32(10, 200, 30, 200)]),
        "pattern" => Brush.Pattern(Red, new Rgba32(0, 0, 0, 0), new bool[,] { { true, false, false }, { false, true, true } }),
        "hatch" => Brush.Hatch(HatchStyle.ForwardDiagonal, Yellow, new Rgba32(0, 0, 90, 120)),
        "pattern-scaled" => Brush.Hatch(HatchStyle.Percent20, Red, Blue, Matrix3x2.CreateScale(3.5f) * Matrix3x2.CreateRotation(0.4f)),
        "image" => Brush.FromImage(Texture(), offsetX: 5, offsetY: -9),
        "image-rotated" => Brush.FromImage(Texture(), transform: Matrix3x2.CreateRotation(0.5f) * Matrix3x2.CreateScale(1.7f), tile: false),
        "recolor" => Brush.Recolor(new Rgba32(100, 100, 40, 255), new Rgba32(10, 250, 10, 255), 0.4f),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static Rgba32[] ToRgba(Canvas c)
    {
        var px = new Rgba32[c.Width * c.Height];
        c.CopyTo<Rgba32>(px, new ImageRect(0, 0, c.Width, c.Height));
        return px;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ImageAndCanvasGiveIdenticalBytes(string name)
    {
        foreach (PixelFormat format in new[] { PixelFormat.Rgba8, PixelFormat.Rgb8, PixelFormat.Gray8, PixelFormat.Rgba16 })
        {
            foreach (bool linear in new[] { false, true })
            {
                foreach ((BlendMode blend, float opacity) in new[] { (BlendMode.Normal, 1f), (BlendMode.Multiply, 0.7f) })
                {
                    using var a = new Canvas(W, H, format);
                    a.CopyFrom<Rgba32>(Backdrop(W, H, !format.HasAlpha), new ImageRect(0, 0, W, H));
                    Image viaImage = a.Snapshot().Fill(Make(name), Scene(), true, IntersectionRule.NonZero, opacity, blend, AlphaComposition.SrcOver, linear);
                    using var b = new Canvas(W, H, format);
                    b.CopyFrom<Rgba32>(Backdrop(W, H, !format.HasAlpha), new ImageRect(0, 0, W, H));
                    b.Fill(Make(name), Scene(), true, IntersectionRule.NonZero, opacity, blend, AlphaComposition.SrcOver, linear);
                    Assert.True(viaImage.ToArray<Rgba32>().AsSpan().SequenceEqual(ToRgba(b)), $"{name} {format} linear={linear} {blend}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ResultIsIdenticalAtAnyDegree(string name)
    {
        Image back = Image.FromPixels<Rgba32>(Backdrop(W, H, false), W, H);
        Rgba32[] one = back.Fill(Make(name), Scene()).ToArray<Rgba32>(maxDegree: 1);
        Assert.True(one.AsSpan().SequenceEqual(back.Fill(Make(name), Scene()).ToArray<Rgba32>(maxDegree: 8)));
        Rgba32[] lin = back.Fill(Make(name), Scene(), linear: true).ToArray<Rgba32>(maxDegree: 1);
        Assert.True(lin.AsSpan().SequenceEqual(back.Fill(Make(name), Scene(), linear: true).ToArray<Rgba32>(maxDegree: 8)));
    }

    [Fact]
    public void LinearGradientHasTheDocumentedColors()
    {
        // Pixel centers at 0.5, 1.5, ...: position = (x + 0.5) / 10 along a 10-pixel line.
        Image img = Image.FromPixels<Rgba32>(new Rgba32[20], 20, 1).Fill(Brush.LinearGradient(new Vector2(0, 0), new Vector2(10, 0), [new(0f, Red), new(1f, Blue)]), Path.Rectangle(0, 0, 20, 1), antialias: false);
        Rgba32[] px = img.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(242, 0, 13, 255), px[0]);  // 0.05
        Assert.Equal(new Rgba32(115, 0, 140, 255), px[5]); // 0.55
    }

    [Fact]
    public void RepetitionModesPlacePositionsAsDocumented()
    {
        Rgba32[] Row(GradientRepetition rep) => Image.FromPixels<Rgba32>(new Rgba32[40], 40, 1)
            .Fill(Brush.LinearGradient(new Vector2(10, 0), new Vector2(20, 0), [new(0f, Red), new(1f, Blue)], rep), Path.Rectangle(0, 0, 40, 1), antialias: false).ToArray<Rgba32>();

        Rgba32[] none = Row(GradientRepetition.None), repeat = Row(GradientRepetition.Repeat), reflect = Row(GradientRepetition.Reflect), dont = Row(GradientRepetition.DontFill);
        Assert.Equal(Red, none[0]);          // before the start: first color
        Assert.Equal(Blue, none[39]);        // after the end: last color
        Assert.Equal(new Rgba32(0, 0, 0, 0), dont[3]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), dont[30]);
        Assert.Equal(dont[12], none[12]);    // inside, all modes agree
        Assert.Equal(none[12], repeat[12]);
        Assert.Equal(none[12], reflect[12]);

        // Position 1.15 (pixel 21: (21.5 - 10) / 10): repeat shows 0.15, reflect shows 0.85.
        Rgba32 at015 = Row(GradientRepetition.None)[11]; // (11.5 - 10) / 10 = 0.15
        Assert.Equal(at015, repeat[21]);
        Rgba32 at085 = Row(GradientRepetition.None)[18]; // 0.85
        Assert.Equal(at085, reflect[21]);
        // Negative positions continue backward in repeat mode: pixel 8 is -0.15, which looks like 0.85.
        Assert.Equal(at085, repeat[8]);
    }

    [Fact]
    public void HardStopsMakeSharpEdges()
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[10], 10, 1)
            .Fill(Brush.LinearGradient(new Vector2(0, 0), new Vector2(10, 0), [new(0f, Red), new(0.5f, Red), new(0.5f, Blue), new(1f, Blue)]), Path.Rectangle(0, 0, 10, 1), antialias: false).ToArray<Rgba32>();
        Assert.All(px[..5], p => Assert.Equal(Red, p));
        Assert.All(px[5..], p => Assert.Equal(Blue, p));
    }

    [Fact]
    public void SingleStopIsSolid()
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[16], 4, 4)
            .Fill(Brush.RadialGradient(new Vector2(2, 2), 3, [new(0.3f, Yellow)]), Path.Rectangle(0, 0, 4, 4), antialias: false).ToArray<Rgba32>();
        Assert.All(px, p => Assert.Equal(Yellow, p));
    }

    [Fact]
    public void PremultipliedAndStraightAgreeForOpaqueStops()
    {
        Image back = Image.FromPixels<Rgba32>(Backdrop(64, 64, true), 64, 64);
        Rgba32[] a = back.Fill(Brush.LinearGradient(new Vector2(0, 0), new Vector2(64, 64), [new(0f, Red), new(1f, Blue)]), Path.Rectangle(0, 0, 64, 64)).ToArray<Rgba32>();
        Rgba32[] b = back.Fill(Brush.LinearGradient(new Vector2(0, 0), new Vector2(64, 64), [new(0f, Red), new(1f, Blue)], premultiplied: false), Path.Rectangle(0, 0, 64, 64)).ToArray<Rgba32>();
        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void LinearLightGradientInterpolatesInLinearLight()
    {
        var black = new Rgba32(0, 0, 0, 255);
        var white = new Rgba32(255, 255, 255, 255);
        Image back = Image.FromPixels<Rgba32>(new Rgba32[101], 101, 1);
        Brush Gradient() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(101, 0), [new(0f, black), new(1f, white)]);
        Rgba32[] encoded = back.Fill(Gradient(), Path.Rectangle(0, 0, 101, 1), antialias: false).ToArray<Rgba32>();
        Rgba32[] linear = back.Fill(Gradient(), Path.Rectangle(0, 0, 101, 1), antialias: false, linear: true).ToArray<Rgba32>();
        Assert.InRange(encoded[50].R, 127, 129);
        // 0.5 in linear light is 188 (0.735) in sRGB.
        Assert.InRange(linear[50].R, 186, 190);
        Assert.Equal(linear[50].R, linear[50].G);
        Assert.True(linear[10].R > encoded[10].R);
    }

    [Fact]
    public void LinearLightFillWithOneColorGradientMatchesSolid()
    {
        var c = new Rgba32(200, 90, 30, 160);
        Image back = Image.FromPixels<Rgba32>(Backdrop(64, 64, true), 64, 64);
        Path shape = Path.Circle(30, 30, 25);
        Rgba32[] solid = back.Fill(Brush.Solid(c), shape, linear: true).ToArray<Rgba32>();
        Rgba32[] stops = back.Fill(Brush.LinearGradient(new Vector2(0, 0), new Vector2(64, 0), [new(0f, c), new(1f, c)]), shape, linear: true).ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < solid.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(solid[i].R - stops[i].R), Math.Max(Math.Abs(solid[i].G - stops[i].G), Math.Max(Math.Abs(solid[i].B - stops[i].B), Math.Abs(solid[i].A - stops[i].A)))));
        }

        Assert.True(worst <= 1, $"worst {worst}");
    }

    [Fact]
    public void GrayImagesTakeGradients()
    {
        Image img = Image.FromPixels<Rgba32>(Backdrop(64, 64, true), 64, 64).ConvertTo(PixelFormat.Gray8);
        Rgba32[] px = img.Fill(Brush.LinearGradient(new Vector2(0, 0), new Vector2(64, 0), [new(0f, new Rgba32(0, 0, 0, 255)), new(1f, new Rgba32(255, 255, 255, 255))]), Path.Rectangle(0, 0, 64, 64), antialias: false, linear: true).ToArray<Rgba32>();
        Assert.True(px[63].R > px[1].R);
        Assert.Equal(px[5].R, px[5].G);
    }

    [Fact]
    public void PatternCellsLandOnTheImageGrid()
    {
        var cells = new bool[2, 3];
        cells[0, 1] = true; // row 0, column 1
        cells[1, 2] = true;
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[6 * 4], 6, 4)
            .Fill(Brush.Pattern(Red, Blue, cells), Path.Rectangle(0, 0, 6, 4), antialias: false).ToArray<Rgba32>();
        string Row(int y) => string.Concat(px.Skip(y * 6).Take(6).Select(p => p == Red ? '#' : '.'));
        Assert.Equal(".#..#.", Row(0));
        Assert.Equal("..#..#", Row(1));
        Assert.Equal(".#..#.", Row(2));
    }

    [Fact]
    public void PatternTransformScalesCells()
    {
        var cells = new bool[1, 2];
        cells[0, 0] = true;
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[8], 8, 1)
            .Fill(Brush.Pattern(Red, Blue, cells, Matrix3x2.CreateScale(2f)), Path.Rectangle(0, 0, 8, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal("##..##..", string.Concat(px.Select(p => p == Red ? '#' : '.')));
    }

    [Fact]
    public void ImageBrushTilesWithOffsetAndPlacesOnceWhenNotTiled()
    {
        var src = new[] { new Rgba32(1, 0, 0, 255), new Rgba32(2, 0, 0, 255), new Rgba32(3, 0, 0, 255) };
        Image tex = Image.FromPixels<Rgba32>(src, 3, 1);
        Rgba32[] tiled = Image.FromPixels<Rgba32>(new Rgba32[8], 8, 1).Fill(Brush.FromImage(tex, offsetX: 2), Path.Rectangle(0, 0, 8, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal(new byte[] { 2, 3, 1, 2, 3, 1, 2, 3 }, tiled.Select(p => p.R).ToArray());
        Rgba32[] once = Image.FromPixels<Rgba32>(new Rgba32[8], 8, 1).Fill(Brush.FromImage(tex, offsetX: 2, tile: false), Path.Rectangle(0, 0, 8, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 0, 0, 0 }, once.Select(p => p.R).ToArray());
        Rgba32[] big = Image.FromPixels<Rgba32>(new Rgba32[8], 8, 1).Fill(Brush.FromImage(tex, transform: Matrix3x2.CreateScale(2f)), Path.Rectangle(0, 0, 8, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal(new byte[] { 1, 1, 2, 2, 3, 3, 1, 1 }, big.Select(p => p.R).ToArray());
        Rgba32[] part = Image.FromPixels<Rgba32>(new Rgba32[6], 6, 1).Fill(Brush.FromImage(tex, new ImageRect(1, 0, 2, 1)), Path.Rectangle(0, 0, 6, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal(new byte[] { 2, 3, 2, 3, 2, 3 }, part.Select(p => p.R).ToArray());
    }

    [Fact]
    public void ImageBrushDescriptorNamesItsSourceNode()
    {
        Image tex = Texture();
        Image filled = Image.FromPixels<Rgba32>(new Rgba32[64], 8, 8).Fill(Brush.FromImage(tex), Path.Rectangle(1, 1, 5, 5));
        Node fill = filled.Root;
        Assert.Equal(2, fill.Inputs.Length);
        Assert.Same(tex.Root, fill.Inputs[1]);
        Assert.Contains(fill.Descriptor.Parameters, p => p.Name == "brush" && (string)p.Value == "image");
        Assert.Contains(fill.Descriptor.Parameters, p => p.Name == "source" && (long)p.Value == tex.Root.Id);
    }

    [Fact]
    public void EveryBrushDescribesItself()
    {
        foreach (string name in new[] { "linear", "radial-reflect", "elliptic", "path", "pattern", "hatch", "image", "recolor" })
        {
            Image f = Image.FromPixels<Rgba32>(new Rgba32[64], 8, 8).Fill(Make(name), Path.Rectangle(1, 1, 5, 5));
            Assert.NotEmpty(f.Root.Descriptor.Parameters);
            Assert.NotEqual("custom", f.Root.Descriptor.Parameters.First(p => p.Name == "brush").Value);
        }
    }

    [Fact]
    public void RecolorReplacesTheColorAndSparesOthers()
    {
        var src = new Rgba32(120, 60, 200, 255);
        var tgt = new Rgba32(0, 255, 0, 255);
        Rgba32[] back = [src, new Rgba32(122, 60, 198, 255), new Rgba32(255, 60, 0, 255), new Rgba32(0, 0, 0, 255)];
        Rgba32[] px = Image.FromPixels<Rgba32>(back, 4, 1).Fill(Brush.Recolor(src, tgt, 0.05f), Path.Rectangle(0, 0, 4, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal(tgt, px[0]);
        Assert.True(px[1].G > 240 && px[1].R < 5, $"near match is mostly replaced: {px[1]}");
        Assert.Equal(back[2], px[2]);
        Assert.Equal(back[3], px[3]);
        // Threshold 0 changes exact matches only (within float rounding).
        Rgba32[] exact = Image.FromPixels<Rgba32>(back, 4, 1).Fill(Brush.Recolor(src, tgt, 0f), Path.Rectangle(0, 0, 4, 1), antialias: false).ToArray<Rgba32>();
        Assert.Equal(tgt, exact[0]);
        Assert.Equal(back[1], exact[1]);
    }

    [Fact]
    public void RecolorWorksInLinearLight()
    {
        var src = new Rgba32(120, 60, 200, 255);
        Rgba32[] back = [src, new Rgba32(0, 0, 0, 255)];
        Rgba32[] px = Image.FromPixels<Rgba32>(back, 2, 1).Fill(Brush.Recolor(src, new Rgba32(0, 255, 0, 255), 0.05f), Path.Rectangle(0, 0, 2, 1), antialias: false, linear: true).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 255, 0, 255), px[0]);
        Assert.Equal(back[1], px[1]);
    }

    [Fact]
    public void PathGradientIsTransparentOutsideAndExactAtVertices()
    {
        Vector2[] pts = [new(10, 10), new(50, 10), new(50, 50), new(10, 50)];
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[64 * 64], 64, 64).Fill(Brush.PathGradient(pts, [Red, Blue, Yellow, new Rgba32(0, 255, 0, 255)], new Rgba32(255, 255, 255, 255)), Path.Rectangle(0, 0, 64, 64), antialias: false).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 0, 0), px[(5 * 64) + 5]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), px[(30 * 64) + 55]);
        Rgba32 corner = px[(10 * 64) + 10];
        Assert.True(corner.R > 240 && corner.G < 10 && corner.B < 10, $"{corner}");
        Rgba32 middle = px[(30 * 64) + 30];
        Assert.True(middle.R > 240 && middle.G > 240 && middle.B > 240, $"{middle}");
    }

    [Fact]
    public void BadArgumentsFailTyped()
    {
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(1, 1), new Vector2(1, 1), [new(0f, Red), new(1f, Blue)]));
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(float.NaN, 1), [new(0f, Red)]));
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(1, 1), []));
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(1, 1), [new(float.NaN, Red)]));
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(1, 1), [new(float.PositiveInfinity, Red)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(1, 1), [new(0f, Red)], (GradientRepetition)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.RadialGradient(new Vector2(0, 0), 0f, [new(0f, Red)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.RadialGradient(new Vector2(0, 0), float.NaN, [new(0f, Red)]));
        Assert.Throws<ArgumentException>(() => Brush.RadialGradient(new Vector2(float.PositiveInfinity, 0), 4f, [new(0f, Red)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.EllipticGradient(new Vector2(0, 0), new Vector2(5, 0), 0f, [new(0f, Red)]));
        Assert.Throws<ArgumentException>(() => Brush.EllipticGradient(new Vector2(0, 0), new Vector2(0, 0), 0.5f, [new(0f, Red)]));
        Assert.Throws<ArgumentException>(() => Brush.PathGradient([new(0, 0), new(1, 1)], [Red, Blue]));
        Assert.Throws<ArgumentException>(() => Brush.PathGradient([new(0, 0), new(1, 1), new(0, 1)], [Red, Blue]));
        Assert.Throws<ArgumentException>(() => Brush.PathGradient([new(0, 0), new(1, float.NaN), new(0, 1)], [Red, Blue, Red]));
        Assert.Throws<ArgumentException>(() => Brush.Pattern(Red, Blue, new bool[0, 3]));
        Assert.Throws<ArgumentException>(() => Brush.Pattern(Red, Blue, new bool[2, 2], new Matrix3x2(0, 0, 0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => Brush.Pattern(Red, Blue, new bool[2, 2], new Matrix3x2(float.NaN, 0, 0, 1, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => Brush.Pattern(Red, Blue, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.Hatch((HatchStyle)99, Red));
        Assert.Throws<ArgumentNullException>(() => Brush.FromImage(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.FromImage(Texture(), new ImageRect(30, 0, 20, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.FromImage(Texture(), new ImageRect(0, 0, 0, 5)));
        Assert.Throws<ArgumentException>(() => Brush.FromImage(Texture(), transform: new Matrix3x2(1, 2, 2, 4, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.Recolor(Red, Blue, -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.Recolor(Red, Blue, float.NaN));
    }

    [Fact]
    public void RowsAtExtremeCoordinatesNeverThrow()
    {
        var row = new float[4 * 9];
        foreach (string name in new[] { "linear", "linear-repeat", "linear-translucent", "radial-reflect", "elliptic", "path", "pattern", "hatch", "pattern-scaled", "image", "image-rotated", "recolor" })
        {
            Brush b = Make(name);
            foreach (int x in new[] { int.MinValue, -1_000_000_000, -5, 0, 400, 1_000_000_000, int.MaxValue - 10 })
            {
                foreach (int y in new[] { int.MinValue, -3, 0, 100, int.MaxValue })
                {
                    if (b.ReadsBackdrop)
                    {
                        b.FillRow(x, y, new float[row.Length], row);
                    }
                    else
                    {
                        b.FillRow(x, y, row);
                    }

                    foreach (float v in row)
                    {
                        Assert.False(float.IsNaN(v), $"{name} at {x},{y}");
                    }
                }
            }
        }
    }

    [Fact]
    public void ExtremeStopPositionsAndHugeGradientsStayFinite()
    {
        var row = new float[4 * 8];
        Brush tiny = Brush.RadialGradient(new Vector2(0, 0), 1e-30f, [new(0f, Red), new(1f, Blue)], GradientRepetition.Repeat);
        tiny.FillRow(0, 0, row);
        Assert.All(row, v => Assert.False(float.IsNaN(v)));
        Brush far = Brush.LinearGradient(new Vector2(0, 0), new Vector2(1e30f, 1e30f), [new(-1e30f, Red), new(1e30f, Blue)]);
        far.FillRow(5, 5, row);
        Assert.All(row, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public void ManyStopsUseBinarySearchAndAgreeWithFewStops()
    {
        var stops = new ColorStop[40];
        for (int i = 0; i < stops.Length; i++)
        {
            stops[i] = new ColorStop(i / 39f, new Rgba32((byte)(i * 6), (byte)(255 - (i * 6)), 7, 255));
        }

        Brush many = Brush.LinearGradient(new Vector2(0, 0), new Vector2(200, 0), stops);
        Brush two = Brush.LinearGradient(new Vector2(0, 0), new Vector2(200, 0), [new(0f, new Rgba32(0, 255, 7, 255)), new(1f, new Rgba32(234, 21, 7, 255))]);
        var a = new float[4 * 200];
        var b = new float[4 * 200];
        many.FillRow(0, 0, a);
        two.FillRow(0, 0, b);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.InRange(a[i], b[i] - 0.01f, b[i] + 0.01f);
        }
    }

    // A brush defined outside the library: the row call is the whole contract.
    private sealed class StripeBrush : Brush
    {
        public override void FillRow(int x, int y, Span<float> rgba)
        {
            for (int i = 0; i < rgba.Length / 4; i++)
            {
                bool on = ((x + i) / 4 % 2) == 0;
                rgba[i * 4] = on ? 1f : 0f;
                rgba[(i * 4) + 1] = 0f;
                rgba[(i * 4) + 2] = on ? 0f : 1f;
                rgba[(i * 4) + 3] = 1f;
            }
        }
    }

    private sealed class InvertBrush : Brush
    {
        public override bool ReadsBackdrop => true;

        public override void FillRow(int x, int y, Span<float> rgba) => rgba.Fill(1f);

        public override void FillRow(int x, int y, ReadOnlySpan<float> backdrop, Span<float> rgba)
        {
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                rgba[i] = 1f - backdrop[i];
                rgba[i + 1] = 1f - backdrop[i + 1];
                rgba[i + 2] = 1f - backdrop[i + 2];
                rgba[i + 3] = 1f;
            }
        }
    }

    [Fact]
    public void CustomBrushesPlugIn()
    {
        Image back = Image.FromPixels<Rgba32>(Backdrop(40, 8, true), 40, 8);
        Rgba32[] px = back.Fill(new StripeBrush(), Path.Rectangle(0, 0, 40, 8), antialias: false).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[0]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[4]);
        Assert.Equal("custom", back.Fill(new StripeBrush(), Path.Rectangle(0, 0, 4, 4)).Root.Descriptor.Parameters.First(p => p.Name == "brush").Value);

        Rgba32[] baseline = back.ToArray<Rgba32>();
        Rgba32[] inverted = back.Fill(new InvertBrush(), Path.Rectangle(0, 0, 40, 8), antialias: false).ToArray<Rgba32>();
        for (int i = 0; i < baseline.Length; i++)
        {
            Assert.InRange(inverted[i].R, 255 - baseline[i].R - 1, 255 - baseline[i].R + 1);
            Assert.Equal(255, inverted[i].A);
        }
    }

    [Fact]
    public void OpaqueBrushOverCoveredPixelsEqualsTheGeneralBlend()
    {
        // The fast path that writes opaque brush pixels directly must agree with the full blend (here forced by a 0.999 opacity).
        Image back = Image.FromPixels<Rgba32>(Backdrop(W, H, true), W, H);
        Rgba32[] direct = back.Fill(Make("linear"), Scene()).ToArray<Rgba32>();
        Rgba32[] general = back.Fill(Make("linear"), Scene(), opacity: 0.99999f).ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < direct.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(direct[i].R - general[i].R), Math.Max(Math.Abs(direct[i].G - general[i].G), Math.Abs(direct[i].B - general[i].B))));
        }

        Assert.True(worst <= 1, $"worst {worst}");
    }

    [Fact]
    public void BrushesLeaveTilesTheShapeDoesNotTouchAlone()
    {
        Image back = Image.FromPixels<Rgba32>(Backdrop(W, H, true), W, H);
        Rgba32[] before = back.ToArray<Rgba32>();
        Rgba32[] after = back.Fill(Make("path"), Path.Circle(40, 40, 20)).ToArray<Rgba32>();
        for (int y = 128; y < H; y++)
        {
            for (int x = 128; x < W; x++)
            {
                Assert.Equal(before[(y * W) + x], after[(y * W) + x]);
            }
        }
    }
}
