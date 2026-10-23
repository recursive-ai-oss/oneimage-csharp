using System.Diagnostics;
using System.Text;
using OneImage.Svg.Dom;
using OneImage.Svg.Render;
using OneImage.Svg.Xml;
using Xunit;

namespace OneImage.Svg.Tests;

/// <summary>Component transfer, morphology, tile, turbulence, displacement and lighting: values, limits and tile independence.</summary>
[Collection(SvgSerial.Name)]
public class SvgFilterEffectsTests
{
    private const string Ns = SvgRenderTests.Ns;

    // A 20 x 20 document at 20 px: pixel (x, y) is unit square (x, y).
    private static Rgba32[] Render(string body) => SvgRenderTests.Pixels($"<svg {Ns} viewBox='0 0 20 20'>{body}</svg>", 20);

    private static Rgba32 At(Rgba32[] px, int x, int y) => px[(y * 20) + x];

    private const string Box = "<rect x='6' y='6' width='8' height='8'";

    [Fact]
    public void ComponentTransferWorksOnStraightColor()
    {
        // Linear on blue: 0.5 * b + 0.25 in sRGB, applied to a half-transparent pixel without touching its alpha.
        string f = "<filter id='f' x='0' y='0' width='1' height='1' color-interpolation-filters='sRGB'><feComponentTransfer><feFuncB type='linear' slope='0.5' intercept='0.25'/><feFuncR type='table' tableValues='1 0'/></feComponentTransfer></filter>";
        Rgba32 p = At(Render(f + $"{Box} fill='rgb(0,0,200)' fill-opacity='0.5' filter='url(#f)'/>"), 10, 10);
        Assert.InRange(p.A, 126, 129);
        Assert.InRange(p.B, 0.5 * 200 + 63 - 2, 0.5 * 200 + 63 + 2); // 0.25 * 255 = 63.75
        Assert.InRange(p.R, 253, 255); // the table 1 -> 0 inverts red
    }

    [Fact]
    public void ComponentTransferTablesAndDiscreteStepsFollowTheSpecification()
    {
        var table = new FeTransfer { Type = FeTransfer.Kind.Table, Table = [0, 1, 0] };
        Assert.Equal(0f, table.Apply(0));
        Assert.Equal(1f, table.Apply(0.5f), 5);
        Assert.Equal(0.5f, table.Apply(0.25f), 5);
        Assert.Equal(0f, table.Apply(1));
        var discrete = new FeTransfer { Type = FeTransfer.Kind.Discrete, Table = [0.1, 0.5, 0.9] };
        Assert.Equal(0.1f, discrete.Apply(0.33f), 5);
        Assert.Equal(0.5f, discrete.Apply(0.34f), 5);
        Assert.Equal(0.9f, discrete.Apply(1f), 5);
        var gamma = new FeTransfer { Type = FeTransfer.Kind.Gamma, Amplitude = 2, Exponent = 2, Offset = -0.5 };
        Assert.Equal(0.5f, gamma.Apply(0.7071068f), 4);
        Assert.Equal(1f, new FeTransfer { Type = FeTransfer.Kind.Linear, Slope = 999999, Intercept = 999999 }.Apply(0.5f));
        Assert.Equal(0.3f, new FeTransfer { Type = FeTransfer.Kind.Table, Table = [0.3] }.Apply(0.9f), 5);
    }

    [Fact]
    public void ErodeAndDilateMoveEdgesByTheRadiusInPixels()
    {
        // A filter region that is the whole 20 x 20 image; radius 2 units = 2 px at this scale.
        string r = "<filter id='{0}' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'><feMorphology operator='{0}' radius='2'/></filter>";
        Rgba32[] erode = Render(string.Format(r, "erode") + $"{Box} fill='blue' filter='url(#erode)'/>");
        Assert.Equal(255, At(erode, 9, 9).A);
        Assert.Equal(0, At(erode, 7, 9).A); // 6 + 2 = 8 is the first column left
        Assert.Equal(255, At(erode, 8, 9).A);
        Rgba32[] dilate = Render(string.Format(r, "dilate") + $"{Box} fill='blue' filter='url(#dilate)'/>");
        Assert.Equal(255, At(dilate, 4, 10).A);
        Assert.Equal(0, At(dilate, 3, 10).A);
        Assert.Equal(255, At(dilate, 15, 10).A);
        Assert.Equal(0, At(dilate, 16, 10).A);
    }

    [Fact]
    public void ZeroOrNegativeRadiusLeavesTheInputAndOneZeroAxisIsSkipped()
    {
        foreach (string radius in new[] { "0", "-3", "", "1 2 3", "-1 4" })
        {
            Rgba32[] px = Render($"<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'><feMorphology radius='{radius}'/></filter>{Box} fill='blue' filter='url(#f)'/>");
            Assert.Equal(255, At(px, 6, 6).A);
            Assert.Equal(0, At(px, 5, 6).A);
        }

        Rgba32[] x = Render($"<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'><feMorphology radius='2 0'/></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.Equal(0, At(x, 7, 10).A);
        Assert.Equal(255, At(x, 9, 6).A); // y is untouched
    }

    [Fact]
    public void AHugeMorphologyRadiusIsCheapAndErasesWhatIsEroded()
    {
        var sw = Stopwatch.StartNew();
        Rgba32[] px = Render($"<filter id='f' filterUnits='userSpaceOnUse' x='-1000000' y='-1000000' width='2000000' height='2000000'><feMorphology radius='1e9'/></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.All(px, p => Assert.Equal(0, p.A));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Rgba32[] dil = Render($"<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'><feMorphology operator='dilate' radius='1e9'/></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.All(dil, p => Assert.Equal(255, p.A)); // the region is filled by what the dilation spreads
    }

    [Fact]
    public void TileRepeatsTheInputsSubregionFromItsCorner()
    {
        string f = "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'><feFlood flood-color='red' x='2' y='2' width='2' height='2'/><feTile/></filter>";
        Rgba32[] px = Render(f + $"{Box} fill='blue' filter='url(#f)'/>");
        Assert.Equal(255, At(px, 2, 2).R);
        Assert.Equal(255, At(px, 4, 2).R); // the next copy starts one tile width right of the first
        Assert.Equal(0, At(px, 3, 3).A == 255 ? 0 : 1); // inside a tile (3,3) is covered by the red flood
        Assert.Equal(255, At(px, 3, 3).R);
        Assert.Equal(255, At(px, 17, 17).R);
        Assert.Equal(255, At(px, 0, 0).R); // tiles extend over the whole region in both directions
    }

    [Fact]
    public void TurbulenceMatchesTheReferenceAlgorithmAndHostileOctavesAreCapped()
    {
        // Values of the reference algorithm (the specification's generator without the redraw step), three octaves; a seed of -20 starts as 21.
        (int Seed, bool Fractal, double[] Expect)[] cases =
        [
            (0, true, [0.331112337, -0.329445239, 0.211274992, -0.152624651]),
            (0, false, [0.487750974, 0.329445239, 0.250839052, 0.225380094]),
            (20, false, [0.115740129, 0.083617877, 0.34659116, 0.368146587]),
            (-20, true, [0.017960066, -0.106728983, 0.037264227, -0.305405345]),
        ];
        foreach ((int seed, bool fractal, double[] expect) in cases)
        {
            var noise = new FilterEffects.Noise(seed);
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal(expect[c], noise.Turbulence(c, 13.37, 41.9, 0.05, 0.05, 3, fractal, false, 0, 0, 0, 0), 7);
            }
        }

        var sw = Stopwatch.StartNew();
        Rgba32[] px = Render("<filter id='f' x='0' y='0' width='1' height='1'><feTurbulence baseFrequency='0.1' numOctaves='2000000000'/></filter>" + $"{Box} fill='blue' filter='url(#f)'/>");
        Assert.Contains(px, p => p.A > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void TurbulenceWithInvalidParametersIsTransparent()
    {
        foreach (string attrs in new[] { "baseFrequency='-0.1'", "baseFrequency='0.1 0.1 0.1'", "baseFrequency='0.1' numOctaves='-1'" })
        {
            Rgba32[] px = Render($"<filter id='f' x='0' y='0' width='1' height='1'><feTurbulence {attrs}/></filter>{Box} fill='blue' filter='url(#f)'/>");
            Assert.All(px, p => Assert.Equal(0, p.A));
        }
    }

    [Fact]
    public void DisplacementMovesPixelsByTheMapAndAScaleOfZeroChangesNothing()
    {
        // Red of the map is 1 everywhere (flood): a shift of scale * (1 - 0.5) = 2 px to the right, so the source is read from the left.
        string map = "<feFlood flood-color='#ff0000' result='m'/>";
        string f = $"<filter id='f' x='-0.5' y='-0.5' width='2' height='2' color-interpolation-filters='sRGB'>{map}<feDisplacementMap in='SourceGraphic' in2='m' scale='4' xChannelSelector='R' yChannelSelector='B'/></filter>";
        Rgba32[] px = Render(f + $"<rect x='6' y='6' width='8' height='8' fill='blue' filter='url(#f)'/>");
        Assert.Equal(255, At(px, 10, 10).A);
        Assert.Equal(255, At(px, 5, 10).A); // each pixel takes the source two to its right, so the box appears two pixels left
        Assert.Equal(0, At(px, 13, 10).A);
        Rgba32[] none = Render(f.Replace("scale='4'", "scale='0'", StringComparison.Ordinal) + $"<rect x='6' y='6' width='8' height='8' fill='blue' filter='url(#f)'/>");
        Assert.Equal(255, At(none, 6, 10).A);
    }

    [Fact]
    public void LightingProducesOpaqueLitSurfacesAndTheRightErrorsForBadInput()
    {
        string diffuse = "<filter id='f' x='0' y='0' width='1' height='1' color-interpolation-filters='sRGB'><feDiffuseLighting lighting-color='white'><feDistantLight azimuth='0' elevation='90'/></feDiffuseLighting></filter>";
        Rgba32[] flat = Render(diffuse + $"{Box} fill='blue' filter='url(#f)'/>");
        Assert.Equal(new Rgba32(255, 255, 255, 255), At(flat, 10, 10)); // light straight on a flat surface: N.L = 1
        Rgba32[] noLight = Render($"<filter id='f'><feDiffuseLighting/></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.All(noLight, p => Assert.Equal(0, p.A));
        Rgba32[] badExponent = Render($"<filter id='f'><feSpecularLighting specularExponent='256'><fePointLight x='1' y='1' z='1'/></feSpecularLighting></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.All(badExponent, p => Assert.Equal(0, p.A));
        Rgba32[] negative = Render($"<filter id='f' x='0' y='0' width='1' height='1' color-interpolation-filters='sRGB'><feDiffuseLighting diffuseConstant='-1'><feDistantLight elevation='90'/></feDiffuseLighting></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.Equal(new Rgba32(0, 0, 0, 255), At(negative, 10, 10));
    }

    [Fact]
    public void SpecularLightingAddsTheMaxOfItsColorAsAlpha()
    {
        string f = "<filter id='f' x='0' y='0' width='1' height='1' color-interpolation-filters='sRGB'><feSpecularLighting lighting-color='rgb(200,100,0)' specularExponent='1'><feDistantLight elevation='90'/></feSpecularLighting></filter>";
        Rgba32 p = At(Render(f + $"{Box} fill='blue' filter='url(#f)'/>"), 10, 10);
        Assert.InRange(p.A, 198, 202);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void ThePartTwoPrimitivesMatchTheWholeRenderInRegions()
    {
        // Each primitive is drawn at 3 x, once whole and once in awkward regions; margins must make them agree. The group is rotated so that
        // the filter works on its own axes and is drawn back through the remaining transform.
        const string Gradient = "<radialGradient id='rg' r='0.5'><stop offset='0.5' stop-color='white'/><stop offset='1' stop-color='black' stop-opacity='0'/></radialGradient>";
        string svg = $"<svg {Ns} viewBox='0 0 200 200' width='200' height='200'>{Gradient}"
            + "<filter id='turb' x='0' y='0' width='1' height='1'><feTurbulence baseFrequency='0.03 0.05' numOctaves='3' seed='7'/></filter>"
            + "<filter id='turbs' x='0' y='0' width='1' height='1'><feTurbulence type='fractalNoise' baseFrequency='0.04' numOctaves='2' stitchTiles='stitch'/></filter>"
            + "<filter id='morph'><feMorphology radius='4 2'/><feMorphology operator='dilate' radius='3'/></filter>"
            + "<filter id='tile' x='0' y='0' width='1' height='1'><feFlood flood-color='navy' x='10' y='10' width='12' height='9'/><feOffset dx='4' dy='3'/><feTile/></filter>"
            + "<filter id='disp' x='-0.1' y='-0.1' width='1.2' height='1.2'><feTurbulence baseFrequency='0.05' numOctaves='2' result='n'/><feDisplacementMap in='SourceGraphic' in2='n' scale='12' xChannelSelector='R' yChannelSelector='G'/></filter>"
            + "<filter id='light' color-interpolation-filters='sRGB'><feDiffuseLighting surfaceScale='5' result='d'><fePointLight x='60' y='120' z='30'/></feDiffuseLighting><feSpecularLighting in='SourceAlpha' specularExponent='20' lighting-color='gold' kernelUnitLength='2'><feSpotLight x='140' y='140' z='40' pointsAtX='100' pointsAtY='100' pointsAtZ='0' limitingConeAngle='50'/></feSpecularLighting><feComposite in2='d' operator='arithmetic' k2='1' k3='1'/></filter>"
            + "<filter id='ct'><feComponentTransfer><feFuncR type='gamma' exponent='0.5'/><feFuncA type='discrete' tableValues='0 0.5 1'/></feComponentTransfer></filter>"
            + "<rect x='10' y='10' width='50' height='40' fill='teal' filter='url(#turb)'/>"
            + "<rect x='70' y='10' width='50' height='40' fill='teal' filter='url(#turbs)'/>"
            + "<circle cx='160' cy='35' r='22' fill='crimson' filter='url(#morph)'/>"
            + "<rect x='10' y='60' width='60' height='40' fill='plum' filter='url(#tile)'/>"
            + "<g transform='translate(110 80) rotate(25)'><rect x='-5' y='-5' width='50' height='40' fill='url(#rg)' filter='url(#disp)'/></g>"
            + "<rect x='20' y='110' width='80' height='80' fill='url(#rg)' filter='url(#light)'/>"
            + "<circle cx='150' cy='150' r='30' fill='orange' fill-opacity='0.7' filter='url(#ct)'/>"
            + "</svg>";
        XmlDocument xml = XmlParser.Parse(Encoding.UTF8.GetBytes(svg), SvgLimits.Default);
        SvgScene scene = SceneBuilder.Build(SvgDocument.Build(xml), SvgLimits.Default, CancellationToken.None);
        const int Size = 600;
        var full = new SvgFilterTests.Capture(Size, Size);

        // The whole render is computed without shared blocks; the regions below are computed with them.
        FilterBlockCache.Disabled = true;
        try
        {
            SceneRenderer.Render(scene, Size, Size, new ImageRect(0, 0, Size, Size), full, new SvgLimits(), CancellationToken.None);
        }
        finally
        {
            FilterBlockCache.Disabled = false;
        }

        Assert.True(full.Pixels.Count(p => p.A > 0) > 100_000, "the drawing is mostly there");

        foreach (ImageRect region in new[] { new ImageRect(0, 0, 128, 128), new ImageRect(97, 61, 203, 150), new ImageRect(300, 300, 250, 250), new ImageRect(511, 17, 89, 301), new ImageRect(33, 330, 301, 211), new ImageRect(250, 90, 257, 190) })
        {
            var part = new SvgFilterTests.Capture(region.Width, region.Height);
            SceneRenderer.Render(scene, Size, Size, region, part, new SvgLimits(), CancellationToken.None);
            int max = 0;
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    Rgba32 a = part.Pixels[(y * region.Width) + x];
                    Rgba32 b = full.Pixels[((region.Y + y) * Size) + region.X + x];
                    max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
                }
            }

            Assert.True(max <= 1, $"region {region} differs from the whole render by {max}");
        }
    }

    [Fact]
    public void AHugeKernelOrderOrMatrixIsRefusedCheaplyAndLargeLightKernelsAreBounded()
    {
        var sw = Stopwatch.StartNew();
        string big = string.Join(' ', Enumerable.Repeat("1", 5000));
        Rgba32[] px = Render($"<filter id='f'><feConvolveMatrix order='5000 1' kernelMatrix='{big}'/></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.All(px, p => Assert.Equal(0, p.A)); // an order beyond the supported kernel size is an invalid primitive
        px = Render($"<filter id='f'><feConvolveMatrix order='1000000000'/></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.All(px, p => Assert.Equal(0, p.A));
        px = Render($"<filter id='f' x='0' y='0' width='1' height='1'><feDiffuseLighting kernelUnitLength='1e9'><feDistantLight/></feDiffuseLighting></filter>{Box} fill='blue' filter='url(#f)'/>");
        Assert.Contains(px, p => p.A > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");

        // A 4096-cell kernel on a region of a few million pixels is more work than the limit allows and fails before computing.
        string kernel = string.Join(' ', Enumerable.Repeat("0.0002", 4096));
        string svg = $"<svg {Ns} viewBox='0 0 2000 2000'><filter id='f' x='0' y='0' width='1' height='1'><feConvolveMatrix order='64' kernelMatrix='{kernel}'/></filter><rect width='2000' height='2000' fill='blue' filter='url(#f)'/></svg>";
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Load(svg).Resize(width: 2000).ToArray<Rgba32>());
    }

    [Fact]
    public void ARegionBeyondThePixelLimitFailsTypedForWholeRegionPrimitives()
    {
        string svg = $"<svg {Ns} viewBox='0 0 20 20'><filter id='f' filterUnits='userSpaceOnUse' x='-5000' y='-5000' width='10000' height='10000'><feFlood x='0' y='0' width='4' height='4'/><feTile/></filter>{Box} fill='blue' filter='url(#f)'/></svg>";
        var strict = new ImageConfiguration().Add(new SvgFormat(new SvgLimits { MaxFilterPixels = 1_000_000 }));
        Image img = Image.Load(Encoding.UTF8.GetBytes(svg), new LoadOptions { Configuration = strict });
        Assert.Throws<ImageLimitException>(() => img.Resize(width: 20).ToArray<Rgba32>());
    }

    [Fact]
    public void BlocksAreComputedOnceAndSharedByTilesAndThreads()
    {
        // A blur of 20 units at 3 x has a margin near 180 px, far more than a 128 px tile: tile requests share 1024 px blocks.
        string svg = $"<svg {Ns} viewBox='0 0 400 300' width='1200' height='900'><filter id='f' x='-0.05' y='-0.05' width='1.1' height='1.1'><feGaussianBlur stdDeviation='20'/><feOffset dx='7' dy='5'/></filter>"
            + "<g filter='url(#f)'><circle cx='120' cy='110' r='60' fill='crimson'/><rect x='200' y='80' width='150' height='170' rx='20' fill='teal'/><path d='M20 280 L120 150 L220 280 Z' fill='gold'/></g></svg>";
        Rgba32[] all;
        FilterBlockCache.Disabled = true;
        try
        {
            all = SvgRenderTests.Load(svg).ToArray<Rgba32>();
        }
        finally
        {
            FilterBlockCache.Disabled = false;
        }

        Image img = SvgRenderTests.Load(svg);
        long before = FilterBlockCache.Computed;
        Rgba32[] tiled = img.ToArray<Rgba32>();
        long blocks = FilterBlockCache.Computed - before;
        int tiledMax = 0;
        for (int i = 0; i < all.Length; i++)
        {
            tiledMax = Math.Max(tiledMax, Math.Max(Math.Max(Math.Abs(all[i].R - tiled[i].R), Math.Abs(all[i].G - tiled[i].G)), Math.Max(Math.Abs(all[i].B - tiled[i].B), Math.Abs(all[i].A - tiled[i].A))));
        }

        Assert.True(tiledMax <= 1, $"blocks differ from the unshared render by {tiledMax}");
        Assert.InRange(blocks, 1, 4); // the drawing spans at most 2 x 2 blocks of 1024 px
        Assert.Contains(all, p => p.A is > 0 and < 255);

        // The same pixels through awkward regions pulled by several threads at once.
        var rnd = new Random(5);
        var rects = new ImageRect[24];
        for (int i = 0; i < rects.Length; i++)
        {
            int x = rnd.Next(0, 1100);
            int y = rnd.Next(0, 800);
            rects[i] = new ImageRect(x, y, rnd.Next(1, Math.Min(300, 1200 - x)), rnd.Next(1, Math.Min(260, 900 - y)));
        }

        int worst = 0;
        Parallel.For(0, rects.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            ImageRect r = rects[i];
            var part = new Rgba32[r.Width * r.Height];
            img.CopyPixelsTo<Rgba32>(part, r);
            int max = 0;
            for (int y = 0; y < r.Height; y++)
            {
                for (int x = 0; x < r.Width; x++)
                {
                    Rgba32 a = part[(y * r.Width) + x];
                    Rgba32 b = all[((r.Y + y) * 1200) + r.X + x];
                    max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
                }
            }

            int seen;
            do
            {
                seen = Volatile.Read(ref worst);
            }
            while (max > seen && Interlocked.CompareExchange(ref worst, max, seen) != seen);
        });
        Assert.True(worst <= 1, $"a region differs from the whole render by {worst}");
        Assert.True(FilterBlockCache.HeldBytes <= 96L * 1024 * 1024);
    }

    [Fact]
    public void ConcurrentRendersOfSeveralFiltersKeepTheBlockCacheWithinItsBudgetAndCorrect()
    {
        // Many different filtered layers rendered from many threads, repeatedly, so blocks are evicted and recomputed while others read them.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 1000 1000' width='3000' height='3000'>");
        for (int i = 0; i < 6; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<filter id='f{i}' x='-0.2' y='-0.2' width='1.4' height='1.4'><feGaussianBlur stdDeviation='{10 + (i * 7)}'/></filter><circle cx='{100 + (i * 150)}' cy='{120 + (i * 140)}' r='90' fill='hsl({i * 50} 70% 50%)' filter='url(#f{i})'/>");
        }

        sb.Append("</svg>");
        Rgba32[] all;
        FilterBlockCache.Disabled = true;
        try
        {
            all = SvgRenderTests.Load(sb.ToString()).ToArray<Rgba32>();
        }
        finally
        {
            FilterBlockCache.Disabled = false;
        }

        Image img = SvgRenderTests.Load(sb.ToString());
        for (int round = 0; round < 3; round++)
        {
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var rnd = new Random((round * 100) + i);
                int x = rnd.Next(0, 2500);
                int y = rnd.Next(0, 2500);
                var r = new ImageRect(x, y, Math.Min(400, 3000 - x), Math.Min(400, 3000 - y));
                var part = new Rgba32[r.Width * r.Height];
                img.CopyPixelsTo<Rgba32>(part, r);
                for (int yy = 0; yy < r.Height; yy++)
                {
                    for (int xx = 0; xx < r.Width; xx++)
                    {
                        Rgba32 a = part[(yy * r.Width) + xx];
                        Rgba32 b = all[((r.Y + yy) * 3000) + r.X + xx];
                        Assert.True(Math.Abs(a.A - b.A) <= 1 && Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1, $"pixel {r.X + xx},{r.Y + yy}");
                    }
                }
            });
        }

        Assert.True(FilterBlockCache.HeldBytes <= 96L * 1024 * 1024);
    }
}
