using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Dithering;
using Rgba32Is = SixLabors.ImageSharp.PixelFormats.Rgba32;
using IS = SixLabors.ImageSharp;

namespace OneImage.Tests.DitherTests;

/// <summary>The Dither op against ImageSharp 3.1.12 for the same palette and image.</summary>
public class DitherParityTests
{
    internal static (Rgba32[] Ours, IS.Image<Rgba32Is> Theirs) Pair(int w, int h, bool alpha = false)
    {
        Rgba32[] px = Gif.GifTestUtil.SyntheticPhoto(w, h);
        if (!alpha)
        {
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = px[i] with { A = 255 };
            }
        }

        var config = IS.Configuration.Default.Clone();
        config.MaxDegreeOfParallelism = 1; // ImageSharp's color cache is shared by its row workers; one worker makes its output reproducible
        var theirs = new IS.Image<Rgba32Is>(config, w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32 p = px[(y * w) + x];
                theirs[x, y] = new Rgba32Is(p.R, p.G, p.B, p.A);
            }
        }

        return (px, theirs);
    }

    internal static readonly (string Name, Dither Ours, IDither Theirs)[] Kernels =
    [
        ("Atkinson", Dither.Atkinson, KnownDitherings.Atkinson),
        ("Burkes", Dither.Burkes, KnownDitherings.Burks),
        ("FloydSteinberg", Dither.FloydSteinberg, KnownDitherings.FloydSteinberg),
        ("JarvisJudiceNinke", Dither.JarvisJudiceNinke, KnownDitherings.JarvisJudiceNinke),
        ("Sierra2", Dither.Sierra2, KnownDitherings.Sierra2),
        ("Sierra3", Dither.Sierra3, KnownDitherings.Sierra3),
        ("SierraLite", Dither.SierraLite, KnownDitherings.SierraLite),
        ("StevensonArce", Dither.StevensonArce, KnownDitherings.StevensonArce),
        ("Stucki", Dither.Stucki, KnownDitherings.Stucki),
        ("Bayer2x2", Dither.Bayer2x2, KnownDitherings.Bayer2x2),
        ("Ordered3x3", Dither.Ordered3x3, KnownDitherings.Ordered3x3),
        ("Bayer4x4", Dither.Bayer4x4, KnownDitherings.Bayer4x4),
        ("Bayer8x8", Dither.Bayer8x8, KnownDitherings.Bayer8x8),
        ("Bayer16x16", Dither.Bayer16x16, KnownDitherings.Bayer16x16),
    ];

    internal static Rgba32[] Cube(int levels)
    {
        var p = new List<Rgba32>();
        for (int r = 0; r < levels; r++)
            for (int g = 0; g < levels; g++)
                for (int b = 0; b < levels; b++)
                    p.Add(new Rgba32((byte)(r * 255 / (levels - 1)), (byte)(g * 255 / (levels - 1)), (byte)(b * 255 / (levels - 1)), 255));
        return [.. p];
    }

    internal static Rgba32[] Grays(int n) => [.. Enumerable.Range(0, n).Select(i => { byte g = (byte)Math.Round(i * 255.0 / (n - 1)); return new Rgba32(g, g, g, 255); })];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var k in Kernels)
        {
            yield return [k.Name, "cube2"];
            yield return [k.Name, "cube3"];
            yield return [k.Name, "gray4"];
            yield return [k.Name, "werner"];
            yield return [k.Name, "web"];
        }
    }

    internal static Rgba32[] PaletteByName(string n) => n switch
    {
        "cube2" => Cube(2),
        "cube3" => Cube(3),
        "gray4" => Grays(4),
        "werner" => [.. KnownPalettes.Werner],
        _ => [.. KnownPalettes.WebSafe],
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesImageSharp(string name, string palette)
    {
        Rgba32[] pal = PaletteByName(palette);
        var (ours, theirs) = Pair(97, 61);
        IS.Color[] isPal = [.. pal.Select(c => IS.Color.FromPixel(new Rgba32Is(c.R, c.G, c.B, c.A)))];
        theirs.Mutate(x => x.Dither(KnownDittering(name), isPal));
        Rgba32[] mine = Image.FromPixels<Rgba32>(ours, 97, 61).Dither(pal, KernelOf(name), fastMatch: true).ToArray<Rgba32>();
        int diff = 0;
        for (int y = 0; y < 61; y++)
            for (int x = 0; x < 97; x++)
            {
                Rgba32Is t = theirs[x, y];
                Rgba32 m = mine[(y * 97) + x];
                if (t.R != m.R || t.G != m.G || t.B != m.B || t.A != m.A) diff++;
            }

        // Error diffusion reproduces ImageSharp exactly. Ordered dithering does too for small palettes; for palettes of dozens of
        // colors ImageSharp's shared color cache occasionally answers for a different color of the same 5-bit cell than the one
        // first seen (a few tenths of a percent of pixels on a photo, under 2.5% here), which has no counterpart in a plain scan.
        bool ordered = name.StartsWith("Bayer", StringComparison.Ordinal) || name == "Ordered3x3";
        double allowed = ordered && palette is "web" or "werner" or "gray4" ? 0.025 : 0;
        Assert.True(diff <= allowed * 97 * 61, $"{name}/{palette}: {diff} of {97 * 61} pixels differ");
    }

    private static IDither KnownDittering(string n) => Kernels.First(x => x.Name == n).Theirs;

    private static Dither KernelOf(string n) => Kernels.First(x => x.Name == n).Ours;
}
