using Xunit;
using static OneImage.Tests.Tone.ToneUtil;

namespace OneImage.Tests.Tone;

public class ToneFusionTests
{
    private static readonly Func<Image, Image>[] LutChain =
    [
        i => i.Levels(0.05, 0.95, 1.2),
        i => i.Curves([new(0, 0), new(0.3, 0.2), new(0.7, 0.85), new(1, 1)]),
        i => i.BrightnessContrast(0.05, 0.2),
        i => i.Gamma(1.8),
        i => i.Invert(),
    ];

    private static readonly Func<Image, Image>[] MixedChain =
    [
        i => i.Exposure(0.5),
        i => i.HueSaturationLightness(30, 0.2, 0.1),
        i => i.Grayscale(),
        i => i.ChannelMixer([1.1, 0.1, -0.2, 0, 1, 0, 0.3, 0, 0.7]),
        i => i.Threshold(0.45),
        i => i.Posterize(3),
    ];

    private static Image Fused(Image img, Func<Image, Image>[] ops)
    {
        foreach (Func<Image, Image> op in ops)
        {
            img = op(img);
        }

        return img;
    }

    private static Image Unfused(Image img, Func<Image, Image>[] ops)
    {
        foreach (Func<Image, Image> op in ops)
        {
            img = Barrier(op(img));
        }

        return img;
    }

    private static void AssertSameBytes(Image a, Image b)
    {
        Assert.Equal(a.Format, b.Format);
        Assert.Equal(a.Width, b.Width);
        switch (a.Format.Sample)
        {
            case SampleType.U8:
                Assert.Equal(a.ToArray<Rgba32>(), b.ToArray<Rgba32>());
                break;
            case SampleType.U16:
                Assert.Equal(a.ToArray<Rgba64>(), b.ToArray<Rgba64>());
                break;
            default:
                Assert.Equal(a.ToArray<RgbaFloat>(), b.ToArray<RgbaFloat>());
                break;
        }
    }

    [Fact]
    public void FiveOpChainIsOnePassOverTheSource()
    {
        var src = new PatternNode(700, 500);
        Image chain = Fused(Image.FromNode(src), LutChain);
        Node node = chain.Frames[0].Node;
        var tone = Assert.IsType<ToneNode>(node);
        Assert.Equal(5, tone.StepCount);
        Assert.Same(src, tone.Inputs[0]);
        Assert.Equal("fused", tone.Descriptor.Name);
        Assert.Equal(5, tone.StepDescriptors().Count());
        _ = chain.ToArray<Rgba32>(maxDegree: 1);
        Assert.Equal(6 * 4, src.Computed);

        // Format conversions join the chain from either side.
        Image withConvert = Image.FromNode(src).ConvertTo(PixelFormat.Rgb8).Gamma(2).Invert().ConvertTo(PixelFormat.Gray8);
        var t2 = Assert.IsType<ToneNode>(withConvert.Frames[0].Node);
        Assert.Equal(4, t2.StepCount);
        Assert.Same(src, t2.Inputs[0]);
    }

    [Fact]
    public void FusedEqualsOneByOneBitForBit()
    {
        AssertSameBytes(Fused(Make8(opaque: false), LutChain), Unfused(Make8(opaque: false), LutChain));
        AssertSameBytes(Fused(Make8(opaque: false), MixedChain), Unfused(Make8(opaque: false), MixedChain));
        AssertSameBytes(Fused(Make16(opaque: false), LutChain), Unfused(Make16(opaque: false), LutChain));
        AssertSameBytes(Fused(Make16(opaque: false), MixedChain), Unfused(Make16(opaque: false), MixedChain));
        AssertSameBytes(Fused(MakeFloat(), LutChain), Unfused(MakeFloat(), LutChain));
        AssertSameBytes(Fused(MakeFloat(), MixedChain), Unfused(MakeFloat(), MixedChain));

        // Premultiplied storage rounds color * alpha, which the fused path reproduces step by step.
        Func<Image> pre = () => Make8(opaque: false).ConvertTo(PixelFormat.Rgba8Premultiplied);
        Image a = Fused(pre(), MixedChain);
        Image b = Unfused(pre(), MixedChain);
        Assert.Equal(PixelFormat.Rgba8Premultiplied, a.Format);
        Assert.Equal(a.ToArray<Rgba32>(), b.ToArray<Rgba32>());
        Assert.Equal(Fused(pre(), LutChain).ToArray<Rgba32>(), Unfused(pre(), LutChain).ToArray<Rgba32>());
    }

    [Fact]
    public void FormatConversionFusesWithoutChangingBytes()
    {
        Func<Image, Image>[] ops =
        [
            i => i.Invert(),
            i => i.ConvertTo(PixelFormat.Rgba16),
            i => i.Gamma(2),
            i => i.RemoveAlpha(),
            i => i.ConvertTo(PixelFormat.Gray16),
            i => i.Invert(),
            i => i.ConvertTo(PixelFormat.GrayAlpha8),
            i => i.ConvertTo(PixelFormat.Rgba8Premultiplied),
            i => i.Flatten(new Rgba32(10, 20, 30, 0)),
        ];
        Image f = Fused(Make8(opaque: false), ops);
        Image u = Unfused(Make8(opaque: false), ops);
        Assert.Equal(f.Format, u.Format);
        Assert.Equal(f.ToArray<Rgba32>(), u.ToArray<Rgba32>());

        // The fused conversion is the same arithmetic as the stand-alone conversion node.
        foreach (PixelFormat target in new[] { PixelFormat.Gray8, PixelFormat.Rgb16, PixelFormat.RgbaF32, PixelFormat.Rgba8Premultiplied, PixelFormat.GrayAlpha8 })
        {
            Image src = Make8(opaque: false);
            Image viaNode = src.ConvertTo(target);
            Image viaTone = src.Invert().Invert().ConvertTo(target);
            Assert.IsType<ConvertNode>(viaNode.Frames[0].Node);
            Assert.Equal(Bytes(viaNode), Bytes(viaTone));
        }
    }

    private static byte[] Bytes(Image img)
    {
        byte[] buf = new byte[img.Width * img.Height * img.Format.BytesPerPixel];
        Render.ReadRegion(img.Frames[0].Node, new ImageRect(0, 0, img.Width, img.Height), buf, 0, img.Format, new RenderContext(1, TileCache.Global, default));
        return buf;
    }

    [Fact]
    public void ResultsAreIdenticalAcrossThreadCounts()
    {
        Image img = Fused(Make8(700, 400, opaque: false), MixedChain);
        Rgba32[] one = img.ToArray<Rgba32>(maxDegree: 1);
        Rgba32[] many = img.ToArray<Rgba32>(maxDegree: 8);
        Assert.Equal(one, many);
        Image lut = Fused(Make8(700, 400, opaque: false), LutChain);
        Assert.Equal(lut.ToArray<Rgba32>(maxDegree: 1), lut.ToArray<Rgba32>(maxDegree: 8));
    }

    [Fact]
    public void EdgeSizesWork()
    {
        foreach ((int w, int h) in new[] { (1, 1), (7, 3), (127, 129), (128, 128), (129, 1), (33, 257) })
        {
            Image img = Make8(Math.Max(w, 16), Math.Max(h, 16), opaque: false).Crop(0, 0, w, h);
            AssertSameBytes(Fused(img, LutChain), Unfused(img, LutChain));
            AssertSameBytes(Fused(img, MixedChain), Unfused(img, MixedChain));
        }
    }

    [Fact]
    public void OpsReportSerializableDescriptors()
    {
        var src = new PatternNode(64, 64);
        foreach ((Func<Image, Image> op, string name) in new (Func<Image, Image>, string)[]
        {
            (i => i.Grayscale(), "grayscale"),
            (i => i.BrightnessContrast(0.1), "brightness_contrast"),
            (i => i.Exposure(1), "exposure"),
            (i => i.Gamma(2), "gamma"),
            (i => i.Levels(0.1), "levels"),
            (i => i.Curves([new(0, 0.1), new(1, 1)]), "curves"),
            (i => i.HueSaturationLightness(10), "hue_saturation_lightness"),
            (i => i.Invert(), "invert"),
            (i => i.Threshold(), "threshold"),
            (i => i.Posterize(4), "posterize"),
            (i => i.ChannelMixer([0, 1, 0, 1, 0, 0, 0, 0, 1]), "channel_mixer"),
            (i => i.RemoveAlpha(), "remove_alpha"),
            (i => i.Flatten(new Rgba32(1, 2, 3, 255)), "flatten"),
        })
        {
            OpDescriptor d = op(Image.FromNode(src)).Frames[0].Node.Descriptor;
            Assert.Equal(name, d.Name);
            Assert.NotEmpty(d.ToString());
        }
    }

    // ---- alpha ----

    [Fact]
    public void SemiTransparentPixelsKeepTheirColorsTreatment()
    {
        // The color of a pixel must come out the same whatever its alpha, and alpha must not move.
        var rnd = new Random(3);
        var opaque = new Rgba32[2000];
        var semi = new Rgba32[2000];
        for (int i = 0; i < opaque.Length; i++)
        {
            var c = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
            opaque[i] = c;
            semi[i] = c with { A = (byte)rnd.Next(256) };
        }

        Func<Image, Image>[] ops = [.. LutChain, .. MixedChain, i => i.Grayscale(false), i => i.Exposure(-1, false), i => i.Threshold(0.3, true)];
        foreach (Func<Image, Image> op in ops)
        {
            Rgba32[] a = op(Image.FromPixels<Rgba32>(opaque, 50, 40)).ToArray<Rgba32>();
            Rgba32[] b = op(Image.FromPixels<Rgba32>(semi, 50, 40)).ToArray<Rgba32>();
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(a[i] with { A = semi[i].A }, b[i]);
            }
        }
    }

    [Fact]
    public void PremultipliedPixelsGiveTheSameColorsWithinStorageRounding()
    {
        var px = new Rgba32[]
        {
            new(200, 100, 50, 255), new(200, 100, 50, 200), new(200, 100, 50, 128), new(30, 220, 120, 100),
        };
        Image straight = Image.FromPixels<Rgba32>(px, 4, 1);
        foreach (Func<Image, Image> op in new Func<Image, Image>[] { i => i.Gamma(2), i => i.Invert(), i => i.Grayscale(), i => i.BrightnessContrast(0.1, 0.2) })
        {
            Rgba32[] expect = op(straight).ToArray<Rgba32>();
            Image pre = op(straight.ConvertTo(PixelFormat.Rgba8Premultiplied));
            Assert.Equal(PixelFormat.Rgba8Premultiplied, pre.Format);
            Rgba32[] got = pre.ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
            for (int i = 0; i < px.Length; i++)
            {
                Assert.Equal(expect[i].A, got[i].A);
                // Premultiplied 8-bit storage carries 1/alpha-sized color steps; allow what that quantization costs.
                int tol = (int)Math.Ceiling(255.0 / px[i].A) + 1;
                Assert.InRange(Math.Abs(expect[i].R - got[i].R), 0, tol);
                Assert.InRange(Math.Abs(expect[i].G - got[i].G), 0, tol);
                Assert.InRange(Math.Abs(expect[i].B - got[i].B), 0, tol);
            }
        }
    }

    [Fact]
    public void FlattenIsExactOnSimpleCases()
    {
        Rgba32[] px = [new(200, 100, 50, 255), new(200, 100, 50, 0), new(200, 100, 50, 128), new(255, 255, 255, 51), new(0, 0, 0, 255)];
        Image img = Image.FromPixels<Rgba32>(px, 5, 1);
        Image flat = img.Flatten(new Rgba32(10, 20, 30, 7));
        Assert.Equal(PixelFormat.Rgb8, flat.Format);
        Rgb24[] got = flat.ToArray<Rgb24>();
        Assert.Equal(new Rgb24(200, 100, 50), got[0]);
        Assert.Equal(new Rgb24(10, 20, 30), got[1]);
        double a = 128 / 255.0;
        Assert.Equal(new Rgb24((byte)Math.Floor((200 * a) + (10 * (1 - a)) + 0.5), (byte)Math.Floor((100 * a) + (20 * (1 - a)) + 0.5), (byte)Math.Floor((50 * a) + (30 * (1 - a)) + 0.5)), got[2]);
        // White over a 20% alpha on black gives 20% gray.
        Assert.Equal(new Rgb24(51, 51, 51), img.Flatten(new Rgba32(0, 0, 0, 255)).ToArray<Rgb24>()[3]);
        Assert.Equal(new Rgb24(255, 255, 255), img.Flatten(new Rgba32(255, 255, 255, 255)).ToArray<Rgb24>()[3]);

        // 16-bit and gray keep the exact ends too.
        Image g = img.ConvertTo(PixelFormat.GrayAlpha8).Flatten(new Rgba32(255, 255, 255, 255));
        Assert.Equal(PixelFormat.Gray8, g.Format);
        Assert.Equal(255, g.ToArray<Gray8>()[1].V);
        Rgb48[] w = img.ConvertTo(PixelFormat.Rgba16).Flatten(new Rgba32(10, 20, 30, 255)).ToArray<Rgb48>();
        Assert.Equal(new Rgb48(10 * 257, 20 * 257, 30 * 257), w[1]);
        Assert.Equal(new Rgb48(200 * 257, 100 * 257, 50 * 257), w[0]);

        // Images without alpha are returned as they are.
        Image opaque = Make8().RemoveAlpha();
        Assert.Same(opaque.Frames[0].Node, opaque.Flatten(new Rgba32(1, 2, 3, 255)).Frames[0].Node);
    }

    [Fact]
    public void AddAndRemoveAlpha()
    {
        Image rgb = Make8().RemoveAlpha();
        Assert.Equal(PixelFormat.Rgb8, rgb.Format);
        Image half = rgb.AddAlpha(0.5);
        Assert.Equal(PixelFormat.Rgba8, half.Format);
        Rgba32[] got = half.ToArray<Rgba32>();
        Rgb24[] src = rgb.ToArray<Rgb24>();
        Assert.Equal(new Rgba32(src[17].R, src[17].G, src[17].B, 128), got[17]);
        Assert.Equal(255, rgb.AddAlpha().ToArray<Rgba32>()[3].A);

        // Removing alpha keeps color as stored (no flattening), including from premultiplied pixels (un-multiplied first).
        Rgba32[] px = [new(200, 100, 50, 128)];
        Rgb24[] dropped = Image.FromPixels<Rgba32>(px, 1, 1).RemoveAlpha().ToArray<Rgb24>();
        Assert.Equal(new Rgb24(200, 100, 50), dropped[0]);
        Rgb24 fromPremultiplied = Image.FromPixels<Rgba32>(px, 1, 1).ConvertTo(PixelFormat.Rgba8Premultiplied).RemoveAlpha().ToArray<Rgb24>()[0];
        Assert.InRange(Math.Abs(fromPremultiplied.R - 200), 0, 2);
    }

    [Fact]
    public void SplitAndCombineRoundTrip()
    {
        foreach (Image img in new[] { Make8(opaque: false), Make16(opaque: false) })
        {
            Image[] parts = img.SplitChannels();
            Assert.Equal(4, parts.Length);
            Assert.All(parts, p => Assert.Equal(PixelFormat.Gray8.Sample == img.Format.Sample ? PixelFormat.Gray8 : PixelFormat.Gray16, p.Format));
            Image back = Image.CombineChannels(parts);
            Assert.Equal(img.Format, back.Format);
            Assert.Equal(Bytes(img), Bytes(back));
            Image rgb = Image.CombineChannels(parts[..3]);
            Assert.Equal(img.RemoveAlpha().ToArray<Rgb24>(), rgb.ToArray<Rgb24>());
        }

        Image g = Make8().ConvertTo(PixelFormat.Gray8);
        Assert.Single(g.SplitChannels());
        Image ga = Image.CombineChannels([g, g.Invert()]);
        Assert.Equal(PixelFormat.GrayAlpha8, ga.Format);
        Assert.Throws<ArgumentException>(() => Image.CombineChannels([g, g.Crop(0, 0, 10, 10)]));
        Assert.Throws<ImageOperationException>(() => Image.CombineChannels([g, Make8()]));
    }

    [Fact]
    public void AnimatedImagesMapOverEveryFrame()
    {
        Image one = Make8(40, 30);
        Image two = Make8(40, 30, seed: 9);
        var anim = new Image([one.Frames[0].Node, two.Frames[0].Node], [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(60)], 3, one.Metadata, null);
        Image edited = anim.Gamma(2).Invert();
        Assert.Equal(2, edited.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(60), edited.Frames[1].Delay);
        Assert.Equal(two.Gamma(2).Invert().ToArray<Rgba32>(), edited.Frames[1].ToArray<Rgba32>());
        Assert.Equal(one.Gamma(2).Invert().ToArray<Rgba32>(), edited.Frames[0].ToArray<Rgba32>());
    }

    [Fact]
    public void StreamsOverATallImageWithoutMaterializingIt()
    {
        var src = new PatternNode(300, 2000);
        Image img = Fused(Image.FromNode(src), LutChain);
        Rgba32[] part = new Rgba32[300 * 10];
        img.CopyPixelsTo<Rgba32>(part, new ImageRect(0, 1000, 300, 10));
        Assert.True(src.Computed <= 3 * 2, $"computed {src.Computed} source tiles for a 10-row strip");
    }
}
