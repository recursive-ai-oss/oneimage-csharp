using System.Reflection;
using SixLabors.ImageSharp.Processing;
using Xunit;
using static OneImage.Tests.Tone.ToneUtil;
using IsColorMatrix = SixLabors.ImageSharp.ColorMatrix;
using IsImage = SixLabors.ImageSharp.Image;
using IsRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Tests.Tone;

public class ColorMatrixTests
{
    private const int W = 301;
    private const int H = 97;

    private static readonly (string Name, Func<Image, Image> Ours, Action<IImageProcessingContext> Sharp)[] Presets =
    [
        ("sepia", i => i.Sepia(), c => c.Sepia()),
        ("sepia0.35", i => i.Sepia(0.35f), c => c.Sepia(0.35f)),
        ("blackwhite", i => i.BlackWhite(), c => c.BlackWhite()),
        ("kodachrome", i => i.Kodachrome(), c => c.Kodachrome()),
        ("opacity0.4", i => i.Opacity(0.4f), c => c.Opacity(0.4f)),
        ("achromatomaly", i => i.ColorBlindness(ColorBlindnessMode.Achromatomaly), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Achromatomaly)),
        ("achromatopsia", i => i.ColorBlindness(ColorBlindnessMode.Achromatopsia), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Achromatopsia)),
        ("deuteranomaly", i => i.ColorBlindness(ColorBlindnessMode.Deuteranomaly), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Deuteranomaly)),
        ("deuteranopia", i => i.ColorBlindness(ColorBlindnessMode.Deuteranopia), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Deuteranopia)),
        ("protanomaly", i => i.ColorBlindness(ColorBlindnessMode.Protanomaly), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Protanomaly)),
        ("protanopia", i => i.ColorBlindness(ColorBlindnessMode.Protanopia), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Protanopia)),
        ("tritanomaly", i => i.ColorBlindness(ColorBlindnessMode.Tritanomaly), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Tritanomaly)),
        ("tritanopia", i => i.ColorBlindness(ColorBlindnessMode.Tritanopia), c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Tritanopia)),
    ];

    private static IsRgba32[] Sharp(Rgba32[] px, Action<IImageProcessingContext> op)
    {
        var data = new IsRgba32[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            data[i] = new IsRgba32(px[i].R, px[i].G, px[i].B, px[i].A);
        }

        using SixLabors.ImageSharp.Image<IsRgba32> img = IsImage.LoadPixelData<IsRgba32>(data, W, H);
        img.Mutate(op);
        var result = new IsRgba32[px.Length];
        img.CopyPixelDataTo(result);
        return result;
    }

    private static int MaxDiff(Rgba32[] ours, IsRgba32[] theirs)
    {
        int m = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            m = Math.Max(m, Math.Abs(ours[i].R - theirs[i].R));
            m = Math.Max(m, Math.Abs(ours[i].G - theirs[i].G));
            m = Math.Max(m, Math.Abs(ours[i].B - theirs[i].B));
            m = Math.Max(m, Math.Abs(ours[i].A - theirs[i].A));
        }

        return m;
    }

    [Fact]
    public void PresetsMatchImageSharpWithinOneStep()
    {
        Rgba32[] px = Pixels8(W, H, 21, opaque: false);
        foreach (var p in Presets)
        {
            Rgba32[] ours = p.Ours(Image.FromPixels<Rgba32>(px, W, H)).ToArray<Rgba32>();
            int d = MaxDiff(ours, Sharp(px, p.Sharp));
            Assert.True(d <= 1, $"{p.Name}: max difference {d}");
        }
    }

    [Fact]
    public void PresetsMatchImageSharpOnOpaqueRgb()
    {
        Rgba32[] px = Pixels8(W, H, 22);
        Rgb24[] rgb = px.Select(static p => new Rgb24(p.R, p.G, p.B)).ToArray();
        foreach (var p in Presets.Where(static p => p.Name != "opacity0.4"))
        {
            Image ours = p.Ours(Image.FromPixels<Rgb24>(rgb, W, H));
            Assert.False(ours.Format.HasAlpha, p.Name);
            int d = MaxDiff(ours.ToArray<Rgba32>(), Sharp(px, p.Sharp));
            Assert.True(d <= 1, $"{p.Name}: max difference {d}");
        }
    }

    [Fact]
    public void GeneralMatrixWithAlphaAndOffsetsMatchesImageSharp()
    {
        var m = new ColorMatrix(
            0.9f, 0.1f, -0.2f, 0.05f,
            0.3f, 0.6f, 0.2f, 0.1f,
            -0.1f, 0.4f, 1.1f, 0f,
            0.2f, -0.1f, 0.15f, 0.8f,
            0.05f, -0.1f, 0.2f, 0.1f);
        Rgba32[] px = Pixels8(W, H, 23, opaque: false);
        var theirs = new IsColorMatrix(
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44, m.M51, m.M52, m.M53, m.M54);
        int d = MaxDiff(Image.FromPixels<Rgba32>(px, W, H).ApplyColorMatrix(m).ToArray<Rgba32>(), Sharp(px, c => c.Filter(theirs)));
        Assert.True(d <= 1, $"max difference {d}");
    }

    [Fact]
    public void PresetValuesAreImageSharpsKnownMatrices()
    {
        Type known = typeof(IsImage).Assembly.GetType("SixLabors.ImageSharp.Processing.KnownFilterMatrices")!;
        const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach ((string name, ColorMatrix ours) in new (string, ColorMatrix)[]
        {
            ("BlackWhiteFilter", ColorMatrix.BlackWhite),
            ("KodachromeFilter", ColorMatrix.Kodachrome),
            ("LomographFilter", ColorMatrix.Lomograph),
            ("PolaroidFilter", ColorMatrix.Polaroid),
            ("AchromatomalyFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Achromatomaly)),
            ("AchromatopsiaFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Achromatopsia)),
            ("DeuteranomalyFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Deuteranomaly)),
            ("DeuteranopiaFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Deuteranopia)),
            ("ProtanomalyFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Protanomaly)),
            ("ProtanopiaFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Protanopia)),
            ("TritanomalyFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Tritanomaly)),
            ("TritanopiaFilter", ColorMatrix.ColorBlindness(ColorBlindnessMode.Tritanopia)),
        })
        {
            object theirs = known.GetProperty(name, F)!.GetValue(null)!;
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float v = (float)theirs.GetType().GetField($"M{r + 1}{c + 1}")!.GetValue(theirs)!;
                    Assert.True(v == ours[r, c], $"{name} M{r + 1}{c + 1}: {v} vs {ours[r, c]}");
                }
            }
        }

        foreach (float amount in new[] { 0f, 0.2f, 0.7f, 1f })
        {
            object theirs = known.GetMethod("CreateSepiaFilter", F)!.Invoke(null, [amount])!;
            ColorMatrix ours = ColorMatrix.Sepia(amount);
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float v = (float)theirs.GetType().GetField($"M{r + 1}{c + 1}")!.GetValue(theirs)!;
                    Assert.Equal(v, ours[r, c]);
                }
            }
        }
    }

    [Fact]
    public void LomographMatchesImageSharpAtTheCenter()
    {
        // ImageSharp darkens the corners after the matrix; the middle of the frame is untouched by that.
        const int S = 201;
        Rgba32[] px = Pixels8(S, S, 24);
        foreach ((Func<Image, Image> op, Action<IImageProcessingContext> sharp) in new (Func<Image, Image>, Action<IImageProcessingContext>)[]
        {
            (i => i.Lomograph(), c => c.Lomograph()),
        })
        {
            Rgba32[] ours = op(Image.FromPixels<Rgba32>(px, S, S)).ToArray<Rgba32>();
            var data = px.Select(static p => new IsRgba32(p.R, p.G, p.B, p.A)).ToArray();
            using SixLabors.ImageSharp.Image<IsRgba32> img = IsImage.LoadPixelData<IsRgba32>(data, S, S);
            img.Mutate(sharp);
            var theirs = new IsRgba32[px.Length];
            img.CopyPixelDataTo(theirs);
            int c0 = ((S / 2) * S) + (S / 2);
            int max = Math.Max(Math.Abs(ours[c0].R - theirs[c0].R), Math.Max(Math.Abs(ours[c0].G - theirs[c0].G), Math.Abs(ours[c0].B - theirs[c0].B)));
            Assert.True(max <= 1, $"center difference {max}");
        }
    }

    [Fact]
    public void IdentityAndNoOpPresetsReturnTheInputNode()
    {
        Image src = Make8();
        Node n = src.Frames[0].Node;
        Assert.Same(n, src.ApplyColorMatrix(ColorMatrix.Identity).Frames[0].Node);
        Assert.Same(n, src.Sepia(0f).Frames[0].Node);
        Assert.Same(n, src.Opacity(1f).Frames[0].Node);
        Image gray = src.Grayscale(toGrayFormat: true);
        Assert.Same(gray.Frames[0].Node, gray.ApplyColorMatrix(ColorMatrix.Identity).Frames[0].Node);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image src = Make8();
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Sepia(1.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Sepia(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.Opacity(-0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.ColorBlindness((ColorBlindnessMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.ApplyColorMatrix(new ColorMatrix(float.NaN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.ApplyColorMatrix(new ColorMatrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, float.PositiveInfinity, 0)));
    }

    [Fact]
    public void OpacityAddsAlphaToOpaqueFormatsAndScalesExistingAlpha()
    {
        Image rgb = Image.FromPixels<Rgb24>([new Rgb24(10, 200, 30), new Rgb24(255, 0, 0)], 2, 1);
        Image half = rgb.Opacity(0.5f);
        Assert.True(half.Format.HasAlpha);
        Rgba32[] px = half.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(10, 200, 30, 128), px[0]);
        Assert.Equal(new Rgba32(255, 0, 0, 128), px[1]);

        Image rgba = Image.FromPixels<Rgba32>([new Rgba32(10, 20, 30, 200), new Rgba32(1, 2, 3, 0)], 2, 1).Opacity(0.5f);
        Assert.Equal([new Rgba32(10, 20, 30, 100), new Rgba32(1, 2, 3, 0)], rgba.ToArray<Rgba32>());
    }

    [Fact]
    public void GrayBecomesRgbAndMatrixOutputIsClamped()
    {
        Image gray = Image.FromPixels<Gray8>([new Gray8(0), new Gray8(100), new Gray8(255)], 3, 1);
        Image out1 = gray.BlackWhite();
        Assert.Equal(ColorModel.Rgb, out1.Format.Model);
        Rgba32[] px = out1.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 0, 255), px[0]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), px[2]);

        // 1.5 * 3 * (100/255) - 1 = 0.7647...
        Assert.Equal(195, px[1].R);
    }

    [Fact]
    public void FusedChainEqualsOneByOne()
    {
        Func<Image, Image>[] chain =
        [
            i => i.Exposure(0.3),
            i => i.Sepia(0.6f),
            i => i.BrightnessContrast(0.05, 0.2),
            i => i.ColorBlindness(ColorBlindnessMode.Protanomaly),
            i => i.Opacity(0.7f),
            i => i.Invert(),
            i => i.Kodachrome(),
        ];
        foreach (Image start in new[] { Make8(opaque: false), Make8(), Make16(opaque: false), MakeFloat(), Make8().Grayscale(toGrayFormat: true), Make8().RemoveAlpha() })
        {
            Image fused = start;
            Image stepped = start;
            foreach (Func<Image, Image> op in chain)
            {
                fused = op(fused);
                stepped = Barrier(op(stepped));
            }

            Assert.Equal(fused.Format, stepped.Format);
            Assert.True(fused.Frames[0].Node is ToneNode, "the chain should be one tone node");
            switch (fused.Format.Sample)
            {
                case SampleType.U8:
                    Assert.Equal(fused.ToArray<Rgba32>(), stepped.ToArray<Rgba32>());
                    break;
                case SampleType.U16:
                    Assert.Equal(fused.ToArray<Rgba64>(), stepped.ToArray<Rgba64>());
                    break;
                default:
                    Assert.Equal(fused.ToArray<RgbaFloat>(), stepped.ToArray<RgbaFloat>());
                    break;
            }
        }
    }

    [Fact]
    public void MatrixStaysOnePassWithTheToneChain()
    {
        var src = new PatternNode(400, 300);
        Image chain = Image.FromNode(src).Gamma(1.4).Sepia().Invert();
        Node node = chain.Frames[0].Node;
        Assert.Same(src, node.Inputs[0]);
        Assert.Equal("fused", node.Descriptor.Name);
        Assert.Equal("color_matrix", ((ToneNode)node).StepDescriptors().ElementAt(1).Name);
    }

    [Fact]
    public void DirectKernelMatchesTheFloatPlanePathAtEdgeWidths()
    {
        ColorMatrix general = new(
            1.3f, -0.2f, 0.1f, 0.3f,
            0.1f, 0.7f, 0.4f, -0.1f,
            -0.5f, 0.2f, 1.2f, 0.2f,
            0.3f, 0.1f, -0.2f, 0.6f,
            0.02f, -0.05f, 0.1f, 0.05f);
        foreach (ColorMatrix matrix in new[] { general, ColorMatrix.Sepia(0.5f), ColorMatrix.Opacity(0.3f), ColorMatrix.BlackWhite, ColorMatrix.Kodachrome, ColorMatrix.ColorBlindness(ColorBlindnessMode.Tritanopia) })
        {
            AssertKernelMatchesPlanes(new ColorMatrixStep(matrix));
        }
    }

    private static void AssertKernelMatchesPlanes(ColorMatrixStep step)
    {
        foreach (PixelFormat inF in new[] { PixelFormat.Rgba8, PixelFormat.Rgb8 })
        {
            foreach (int w in new[] { 1, 2, 3, 7, 8, 15, 16, 17, 31, 33, 127, 128 })
            {
                byte[] src = new byte[w * inF.BytesPerPixel];
                new Random(w).NextBytes(src);
                PixelFormat outF = step.Output(inF);
                byte[] direct = new byte[w * outF.BytesPerPixel];
                Assert.True(step.TryApplyBytes(src, direct, inF, w));

                byte[] viaPlanes = new byte[direct.Length];
                Span<float> buf = new float[4 * Tile.Size];
                var row = new ToneRow { Buf = buf, Width = w };
                Kernels.ToneKernel.UnpackRow(src, inF, ref row);
                step.Apply(ref row);
                Kernels.ToneKernel.PackRow(ref row, outF, viaPlanes);
                Assert.Equal(viaPlanes, direct);
            }
        }
    }

    [Fact]
    public void PremultipliedPixelsKeepTheirColorsWithinStorageRounding()
    {
        Rgba32[] px = Pixels8(64, 32, 31, opaque: false);
        Image straight = Image.FromPixels<Rgba32>(px, 64, 32).Sepia();
        Image premult = Image.FromPixels<Rgba32>(px, 64, 32).ConvertTo(PixelFormat.Rgba8Premultiplied).Sepia();
        Assert.Equal(AlphaMode.Premultiplied, premult.Format.Alpha);
        Rgba32[] a = straight.ToArray<Rgba32>();
        Rgba32[] b = premult.ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].A, b[i].A);
            if (a[i].A >= 128)
            {
                Assert.True(Math.Abs(a[i].R - b[i].R) <= 2 && Math.Abs(a[i].G - b[i].G) <= 2 && Math.Abs(a[i].B - b[i].B) <= 2, $"pixel {i}: {a[i]} vs {b[i]}");
            }
        }
    }

    [Fact]
    public void DescriptorIsSerializable()
    {
        OpDescriptor d = Image.FromNode(new PatternNode(16, 16)).Sepia(0.5f).Frames[0].Node.Descriptor;
        Assert.Equal("color_matrix", d.Name);
        Assert.Single(d.Parameters);
        Assert.Equal(20, ((string)d.Parameters[0].Value).Split(',').Length);
    }
}
