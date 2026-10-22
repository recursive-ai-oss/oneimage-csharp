using SkiaSharp;

namespace OneImage.Tests.Jpeg;

/// <summary>Cross-checks against libjpeg-turbo as shipped inside SkiaSharp.</summary>
public class JpegInteropTests
{
    internal static Rgb24[] Photo(int w, int h, int seed)
    {
        // Smooth regions, edges and fine texture: closer to a photograph than noise alone.
        var rnd = new Random(seed);
        var px = new Rgb24[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 128 + (90 * Math.Sin((x / 37.0) + (y / 53.0)));
                double g = 128 + (80 * Math.Cos((y / 29.0) - (x / 71.0)));
                double b = 40 + (x * 150.0 / w) + (y * 60.0 / h);
                if (((x / 64) + (y / 48)) % 3 == 0)
                {
                    r = 240 - (r * 0.3);
                }

                double tex = rnd.Next(-9, 10) + (6 * Math.Sin(x * 1.3) * Math.Cos(y * 0.9));
                px[(y * w) + x] = new Rgb24(Clamp(r + tex), Clamp(g + tex), Clamp(b + tex));
            }
        }

        return px;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static byte[] ToBytes(Rgb24[] px)
    {
        byte[] b = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++)
        {
            b[3 * i] = px[i].R;
            b[(3 * i) + 1] = px[i].G;
            b[(3 * i) + 2] = px[i].B;
        }

        return b;
    }

    private static byte[] SkiaDecodeRgb(byte[] jpeg, out int w, out int h)
    {
        using SKBitmap bmp = SKBitmap.Decode(jpeg);
        w = bmp.Width;
        h = bmp.Height;
        SKColor[] colors = bmp.Pixels;
        byte[] rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[3 * i] = colors[i].Red;
            rgb[(3 * i) + 1] = colors[i].Green;
            rgb[(3 * i) + 2] = colors[i].Blue;
        }

        return rgb;
    }

    private static byte[] SkiaEncode(Rgb24[] px, int w, int h, int quality, SKJpegEncoderDownsample downsample)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            rgba[4 * i] = px[i].R;
            rgba[(4 * i) + 1] = px[i].G;
            rgba[(4 * i) + 2] = px[i].B;
            rgba[(4 * i) + 3] = 255;
        }

        using var data = SKData.CreateCopy(rgba);
        using SKImage image = SKImage.FromPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), data, w * 4);
        using SKPixmap pm = image.PeekPixels();
        using var ms = new MemoryStream();
        using (var wstream = new SKManagedWStream(ms))
        {
            Assert.True(pm.Encode(wstream, new SKJpegEncoderOptions(quality, downsample, SKJpegEncoderAlphaOption.Ignore)));
        }

        return ms.ToArray();
    }

    internal static double Psnr(byte[] a, byte[] b)
    {
        double sse = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sse += d * d;
        }

        return 10 * Math.Log10(255.0 * 255.0 * a.Length / Math.Max(sse, 1e-9));
    }

    [Theory]
    [InlineData(75, JpegSubsampling.Yuv420, false, false)]
    [InlineData(90, JpegSubsampling.Yuv444, false, false)]
    [InlineData(60, JpegSubsampling.Yuv422, true, false)]
    [InlineData(80, JpegSubsampling.Yuv420, false, true)]
    [InlineData(85, JpegSubsampling.Yuv440, false, true)]
    public void ReferenceDecoderReadsOurFilesAndAgrees(int quality, JpegSubsampling sub, bool optimize, bool progressive)
    {
        const int W = 203;
        const int H = 117;
        Rgb24[] px = Photo(W, H, 1);
        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(px, W, H), new JpegEncoderOptions { Quality = quality, Subsampling = sub, OptimizeHuffman = optimize, Progressive = progressive, RestartInterval = 7 });
        byte[] theirs = SkiaDecodeRgb(jpeg, out int w, out int h);
        Assert.Equal((W, H), (w, h));
        (_, byte[] ours, _) = JpegTestSupport.Decode(jpeg);
        int max = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            max = Math.Max(max, Math.Abs(ours[i] - theirs[i]));
        }

        Assert.True(max <= 1, $"max difference {max}");
    }

    // Frames of a megapixel or more transform MCU rows on a second thread; the output must not depend on that.
    [Theory]
    [InlineData(JpegSubsampling.Yuv420)]
    [InlineData(JpegSubsampling.Yuv444)]
    public void LargeFrameMatchesReferenceDecoder(JpegSubsampling sub)
    {
        const int W = 1237;
        const int H = 911;
        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(Photo(W, H, 3), W, H), new JpegEncoderOptions { Quality = 85, Subsampling = sub });
        byte[] theirs = SkiaDecodeRgb(jpeg, out _, out _);
        (_, byte[] ours, _) = JpegTestSupport.Decode(jpeg);
        Assert.Equal(theirs.Length, ours.Length);
        int max = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            max = Math.Max(max, Math.Abs(ours[i] - theirs[i]));
        }

        Assert.True(max <= 1, $"max difference {max}");
    }

    [Fact]
    public void LargeFrameTruncatedFailsTyped()
    {
        const int W = 1237;
        const int H = 911;
        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(Photo(W, H, 3), W, H), new JpegEncoderOptions { Quality = 85 });
        byte[] cut = jpeg.AsSpan(0, jpeg.Length / 2).ToArray();
        Assert.ThrowsAny<ImageFormatException>(() => JpegTestSupport.Decode(cut));
    }

    [Theory]
    [InlineData(75)]
    [InlineData(90)]
    public void SizeAndQualityMatchTheReferenceEncoder(int quality)
    {
        const int W = 640;
        const int H = 480;
        Rgb24[] px = Photo(W, H, 2);
        byte[] src = ToBytes(px);
        byte[] ours = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(px, W, H), new JpegEncoderOptions { Quality = quality });
        byte[] theirs = SkiaEncode(px, W, H, quality, SKJpegEncoderDownsample.Downsample420);
        double ratio = (double)ours.Length / theirs.Length;
        (_, byte[] oursDecoded, _) = JpegTestSupport.Decode(ours);
        (_, byte[] theirsDecoded, _) = JpegTestSupport.Decode(theirs);
        double dPsnr = Psnr(src, oursDecoded) - Psnr(src, theirsDecoded);
        TestContext.Current.SendDiagnosticMessage($"q{quality}: ours {ours.Length} B, reference {theirs.Length} B (ratio {ratio:F4}); PSNR ours {Psnr(src, oursDecoded):F3} dB, reference {Psnr(src, theirsDecoded):F3} dB");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "jpeg-encoder-quality.txt"), $"q{quality} ours={ours.Length} reference={theirs.Length} ratio={ratio:F4} psnr_ours={Psnr(src, oursDecoded):F3} psnr_ref={Psnr(src, theirsDecoded):F3}\n");
        Assert.InRange(ratio, 0.95, 1.05);
        Assert.InRange(Math.Abs(dPsnr), 0, 0.3);
    }
}
