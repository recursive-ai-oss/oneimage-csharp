using System.Numerics;
using OneImage.Drawing;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>Gradient and pattern fill layers that are created or edited in the model are written as fill settings (or, when they cannot be, as pixels).</summary>
public class FillWriteTests
{
    private const int W = 96;
    private const int H = 64;

    private static ColorStop[] Stops(params (float Pos, Rgba32 Color)[] s) => [.. s.Select(static x => new ColorStop(x.Pos, x.Color))];

    private static readonly Rgba32 Red = new(230, 30, 20, 255);
    private static readonly Rgba32 Blue = new(20, 60, 220, 255);
    private static readonly Rgba32 Yellow = new(250, 220, 40, 255);

    private static Rgba32[] Raster(Brush brush)
    {
        using var canvas = new Canvas(W, H, PixelFormat.Rgba8);
        canvas.Fill(brush, Drawing.Path.Rectangle(0, 0, W, H), antialias: false, composition: AlphaComposition.Src);
        return canvas.Snapshot().ToArray<Rgba32>();
    }

    private static int MaxDifference(Rgba32[] a, Rgba32[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)), Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A))));
        }

        return max;
    }

    // Draws the settings a plan wrote the way the reader would (without the pixels Photoshop stores beside them).
    private static Rgba32[] DrawnFromSettings(FillPlan plan)
    {
        var colors = new ColorConverter(ColorProfile.Srgb, null);
        Brush? drawn;
        if (plan.Key == "GdFl")
        {
            GradientFillSpec spec = FillDescriptors.ParseGradient(plan.Data, PsdLimits.Default, colors)!;
            Assert.NotNull(spec);
            drawn = new PsdGradientBrush(spec, new System.Drawing.RectangleF(0, 0, W, H));
        }
        else
        {
            var patterns = new PatternCache([new PsdBlock("Patt", plan.Pattern!.Entry)]);
            drawn = FillDescriptors.MakePatternBrush(plan.Data, PsdLimits.Default, patterns, new System.Drawing.RectangleF(0, 0, W, H), shape: false);
            Assert.NotNull(drawn);
        }

        return Raster(drawn!);
    }

    public static TheoryData<string, int> Gradients => new()
    {
        { "linear-diagonal", 3 },
        { "linear-vertical", 3 },
        { "linear-short", 3 },
        { "linear-steep-reversed", 3 },
        { "radial", 3 },
        { "radial-offset", 3 },
        { "linear-translucent", 3 },
        { "linear-hard-edge", 3 },
    };

    private static Brush GradientBrush(string name) => name switch
    {
        "linear-diagonal" => Brush.LinearGradient(new Vector2(10, 5), new Vector2(80, 60), Stops((0, Red), (0.5f, Yellow), (1, Blue))),
        "linear-vertical" => Brush.LinearGradient(new Vector2(40, 0), new Vector2(40, H), Stops((0, Red), (1, Blue))),
        "linear-short" => Brush.LinearGradient(new Vector2(30, 20), new Vector2(50, 22), Stops((0, Red), (0.3f, Yellow), (1, Blue))),
        "linear-steep-reversed" => Brush.LinearGradient(new Vector2(70, 60), new Vector2(60, 5), Stops((0.1f, Red), (0.9f, Blue))),
        "radial" => Brush.RadialGradient(new Vector2(48, 32), 40, Stops((0, Yellow), (1, Blue))),
        "radial-offset" => Brush.RadialGradient(new Vector2(20, 50), 55, Stops((0, Red), (0.4f, Yellow), (1, Blue))),
        "linear-translucent" => Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), Stops((0, new Rgba32(20, 60, 220, 255)), (1, new Rgba32(20, 60, 220, 0)))),
        "linear-hard-edge" => Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, H), Stops((0, Red), (0.5f, Red), (0.5f, Blue), (1, Blue))),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Gradients))]
    public void AGradientBrushIsWrittenAsSettingsThatDrawTheSameFill(string name, int _)
    {
        Brush brush = GradientBrush(name);
        FillPlan? plan = FillWriter.Map(brush, W, H, 8);
        Assert.NotNull(plan);
        Assert.Equal("GdFl", plan.Key);
        // The table Photoshop's gradient is drawn from has 4096 entries, so smooth gradients agree to a level or two; a hard edge moves by up to a pixel.
        int diff = MaxDifference(Raster(brush), DrawnFromSettings(plan));
        Assert.True(diff <= (name == "linear-hard-edge" ? 255 : 2), $"{name}: max difference {diff}");
        if (name == "linear-hard-edge")
        {
            Rgba32[] a = Raster(brush);
            Rgba32[] b = DrawnFromSettings(plan);
            int off = 0;
            for (int i = 0; i < a.Length; i++)
            {
                off += a[i] == b[i] ? 0 : 1;
            }

            Assert.True(off < a.Length / 20, $"hard edge: {off} pixels differ");
        }
    }

    [Fact]
    public void HatchesAndBitmapPatternsAreWrittenAsPatternFills()
    {
        foreach (Brush brush in new[]
        {
            Brush.Hatch(HatchStyle.ForwardDiagonal, new Rgba32(20, 20, 20, 255), new Rgba32(240, 240, 200, 255)),
            Brush.Pattern(Red, Yellow, new bool[,] { { true, false, false }, { false, true, false } }),
        })
        {
            foreach (int depth in new[] { 8, 16 })
            {
                FillPlan? plan = FillWriter.Map(brush, W, H, depth);
                Assert.NotNull(plan);
                Assert.Equal("PtFl", plan.Key);
                Assert.Equal(0, MaxDifference(Raster(brush), DrawnFromSettings(plan)));
            }
        }
    }

    [Fact]
    public void BrushesWithNoEquivalentAreNotMapped()
    {
        Brush[] none =
        [
            Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), Stops((0, Red), (1, Blue)), GradientRepetition.Repeat),
            Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), Stops((0, Red), (1, Blue)), GradientRepetition.Reflect),
            Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), Stops((-0.5f, Red), (1.5f, Blue))),
            Brush.LinearGradient(new Vector2(0, 0), new Vector2(W, 0), Stops((0, Red), (1, new Rgba32(0, 0, 255, 0)))),
            Brush.EllipticGradient(new Vector2(48, 32), new Vector2(80, 32), 0.5f, Stops((0, Red), (1, Blue))),
            Brush.PathGradient([new Vector2(0, 0), new Vector2(W, 0), new Vector2(W, H)], [Red, Blue, Yellow]),
            Brush.Pattern(Red, new Rgba32(0, 0, 0, 0), new bool[,] { { true, false } }),
            Brush.Pattern(Red, Blue, new bool[,] { { true, false } }, Matrix3x2.CreateScale(2)),
            Brush.Recolor(Red, Blue),
        ];
        foreach (Brush b in none)
        {
            Assert.Null(FillWriter.Map(b, W, H, 8));
        }

        // Neither do pattern fills in a 32-bit document, or gradients on a document too small for Photoshop's whole-pixel layout.
        Assert.Null(FillWriter.Map(Brush.Hatch(HatchStyle.Horizontal, Red, Blue), W, H, 32));
        Assert.Null(FillWriter.Map(GradientBrush("radial"), 12, 12, 8));
    }

    private static PsdFile Document(Brush brush, int depth = 8, Brush? second = null)
    {
        PixelFormat format = depth == 16 ? PixelFormat.Rgba16 : PixelFormat.Rgba8;
        var doc = new Document(W, H, new DocumentOptions { Format = format });
        var file = PsdFile.FromDocument(doc);
        PixelLayer under = doc.Add(doc.CreatePixelLayer("under", W, H, 0, 0));
        under.Pixels.Fill(new Rgba32(240, 240, 240, 255), Drawing.Path.Rectangle(0, 0, W, H));
        doc.Add(doc.CreateFillLayer("fill", brush));
        if (second != null)
        {
            FillLayer f2 = doc.Add(doc.CreateFillLayer("fill 2", second));
            f2.Opacity = 128 / 255f; // the format stores opacity in 8 bits
            f2.BlendMode = BlendMode.Multiply;
        }

        return file;
    }

    [Theory]
    [InlineData("linear-diagonal", 8)]
    [InlineData("radial", 8)]
    [InlineData("linear-translucent", 8)]
    [InlineData("linear-diagonal", 16)]
    public void AGradientFillLayerSurvivesASaveAndLoad(string name, int depth)
    {
        using PsdFile file = Document(GradientBrush(name), depth, second: GradientBrush("radial-offset"));
        byte[] first = Util.Save(file);
        using PsdFile loaded = PsdFile.Load(first);
        FillLayer back = Assert.IsType<FillLayer>(loaded.Document.Root.Children[1]);
        FillLayer back2 = Assert.IsType<FillLayer>(loaded.Document.Root.Children[2]);
        Assert.Equal(PsdLayerKind.GradientOrPatternFill, loaded.GetInfo(back).Kind);
        Assert.Equal(PsdLayerKind.GradientOrPatternFill, loaded.GetInfo(back2).Kind);
        Assert.Contains(loaded.GetInfo(back).Blocks.Concat([loaded.GetInfo(back).SourceFillBlock!]), static b => b.Key == "GdFl");
        Assert.Equal(file.Document.Render().ToArray<Rgba32>(), loaded.Document.Render().ToArray<Rgba32>());
        Assert.True(first.AsSpan().SequenceEqual(Util.Save(loaded)), "second write differs from the first");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void APatternFillLayerSurvivesASaveAndLoad(int depth)
    {
        Brush hatch = Brush.Hatch(HatchStyle.Percent20, new Rgba32(10, 40, 90, 255), new Rgba32(230, 230, 250, 255));
        Brush dots = Brush.Pattern(Red, Yellow, new bool[,] { { true, false, false, false }, { false, false, true, false } });
        using PsdFile file = Document(hatch, depth, dots);
        byte[] first = Util.Save(file);
        using PsdFile loaded = PsdFile.Load(first);
        FillLayer back = Assert.IsType<FillLayer>(loaded.Document.Root.Children[1]);
        Assert.Equal(PsdLayerKind.GradientOrPatternFill, loaded.GetInfo(back).Kind);
        Assert.Equal("PtFl", loaded.GetInfo(back).SourceFillBlock!.Key);
        Assert.Equal(depth == 16 ? "Pat2" : "Patt", Assert.Single(loaded.DocumentBlocks).Key);
        Assert.Equal(file.Document.Render().ToArray<Rgba32>(), loaded.Document.Render().ToArray<Rgba32>());
        Assert.True(first.AsSpan().SequenceEqual(Util.Save(loaded)), "second write differs from the first");

        // The settings alone (no stored pixels) draw the same fill: read the file again with the fills drawn from the pattern resources.
        var patterns = new PatternCache(loaded.DocumentBlocks);
        Brush? drawn = FillDescriptors.MakePatternBrush(loaded.GetInfo(back).SourceFillBlock!.Data.Span, PsdLimits.Default, patterns, new System.Drawing.RectangleF(0, 0, W, H), shape: false);
        Assert.Equal(0, MaxDifference(Raster(hatch), Raster(drawn!)));
    }

    [Fact]
    public void ThePatternIsStoredOnceHoweverOftenItIsUsed()
    {
        Brush hatch = Brush.Hatch(HatchStyle.Percent20, Red, Blue);
        var doc = new Document(W, H);
        using var file = PsdFile.FromDocument(doc);
        doc.Add(doc.CreateFillLayer("a", hatch));
        doc.Add(doc.CreateFillLayer("b", Brush.Hatch(HatchStyle.Percent20, Red, Blue)));
        doc.Add(doc.CreateFillLayer("c", Brush.Hatch(HatchStyle.Horizontal, Red, Blue)));
        using PsdFile loaded = PsdFile.Load(Util.Save(file));
        PsdBlock patt = Assert.Single(loaded.DocumentBlocks);
        // Two entries: the hatch is shared by two layers.
        int entries = 0;
        for (int p = 0; p + 4 <= patt.Data.Length; p += (int)((System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(patt.Data.Span[p..]) + 4 + 3) & ~3u))
        {
            entries++;
        }

        Assert.Equal(2, entries);
        Assert.Empty(file.DocumentBlocks); // the model itself is not changed by saving
    }

    [Fact]
    public void ABrushWithNoEquivalentIsWrittenAsAPixelLayer()
    {
        Brush repeat = Brush.LinearGradient(new Vector2(0, 0), new Vector2(30, 0), Stops((0, Red), (1, Blue)), GradientRepetition.Repeat);
        using PsdFile file = Document(repeat);
        byte[] first = Util.Save(file);
        using PsdFile loaded = PsdFile.Load(first);
        PixelLayer back = Assert.IsType<PixelLayer>(loaded.Document.Root.Children[1]);
        Assert.Equal("fill", back.Name);
        Assert.Equal(file.Document.Render().ToArray<Rgba32>(), loaded.Document.Render().ToArray<Rgba32>());
        Assert.True(first.AsSpan().SequenceEqual(Util.Save(loaded)));
    }

    [Fact]
    public void ReplacingTheBrushOfAFillThatWasReadWritesTheNewOne()
    {
        using PsdFile file = Document(GradientBrush("linear-diagonal"));
        using PsdFile loaded = PsdFile.Load(Util.Save(file));
        FillLayer fill = Assert.IsType<FillLayer>(loaded.Document.Root.Children[1]);

        fill.Brush = GradientBrush("radial");
        using PsdFile again = PsdFile.Load(Util.Save(loaded));
        FillLayer back = Assert.IsType<FillLayer>(again.Document.Root.Children[1]);
        Assert.Equal("GdFl", again.GetInfo(back).SourceFillBlock!.Key);
        Assert.Equal(Raster(GradientBrush("radial")), back.Brush is { } b ? Raster(b) : null);

        fill.Brush = Brush.Hatch(HatchStyle.Vertical, Red, Blue);
        using PsdFile pattern = PsdFile.Load(Util.Save(loaded));
        Assert.Equal("PtFl", pattern.GetInfo(pattern.Document.Root.Children[1]).SourceFillBlock!.Key);

        fill.Brush = Brush.Solid(Yellow);
        using PsdFile solid = PsdFile.Load(Util.Save(loaded));
        Assert.Equal(PsdLayerKind.SolidColorFill, solid.GetInfo(solid.Document.Root.Children[1]).Kind);

        fill.Brush = Brush.Recolor(Red, Blue);
        using PsdFile raster = PsdFile.Load(Util.Save(loaded));
        Assert.IsType<PixelLayer>(raster.Document.Root.Children[1]);
    }

    [Fact]
    public void AFillWithAStyleKeepsItThroughTheWrite()
    {
        using PsdFile file = Document(GradientBrush("radial"));
        FillLayer fill = (FillLayer)file.Document.Root.Children[1];
        fill.Style = new LayerStyle([new StrokeEffect { Size = 3, Color = new Rgba32(0, 0, 0, 255) }]);
        using PsdFile loaded = PsdFile.Load(Util.Save(file));
        Assert.IsType<StrokeEffect>(Assert.Single(loaded.Document.Root.Children[1].Style!.Effects));
        Assert.Equal(file.Document.Render().ToArray<Rgba32>(), loaded.Document.Render().ToArray<Rgba32>());
    }
}
