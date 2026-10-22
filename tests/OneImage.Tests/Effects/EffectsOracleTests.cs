using System.Globalization;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using Xunit;
using IsColor = SixLabors.ImageSharp.Color;
using IsImage = SixLabors.ImageSharp.Image;
using IsRect = SixLabors.ImageSharp.Rectangle;
using IsRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Tests.Effects;

/// <summary>
/// Compares the effect operations with ImageSharp 3.1.12 on the same pixels. Set ONEIMAGE_EFFECTS_REPORT to a file path to
/// append the measured differences (max and mean absolute difference per 8-bit channel value).
/// </summary>
public class EffectsOracleTests
{
    internal const int W = 211;
    internal const int H = 157;

    /// <summary>A test picture: smooth gradients, a few hard-edged shapes, and noise, with the given alpha.</summary>
    internal static byte[] Picture(int w, int h, int seed, byte alpha = 255)
    {
        var rnd = new Random(seed);
        byte[] raw = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                int r = (x * 255 / Math.Max(1, w - 1));
                int g = (y * 255 / Math.Max(1, h - 1));
                int b = 128 + (int)(100 * Math.Sin((x + y) / 9.0));
                if (((x / 24) + (y / 24)) % 3 == 0)
                {
                    r = 255 - r;
                }

                int dx = x - (w / 2);
                int dy = y - (h / 2);
                if ((dx * dx) + (dy * dy) < 30 * 30)
                {
                    r = 240;
                    g = 30;
                    b = 40;
                }

                int n = rnd.Next(-12, 13);
                raw[i] = (byte)Math.Clamp(r + n, 0, 255);
                raw[i + 1] = (byte)Math.Clamp(g + n, 0, 255);
                raw[i + 2] = (byte)Math.Clamp(b + n, 0, 255);
                raw[i + 3] = alpha;
            }
        }

        return raw;
    }

    internal static Image Ours(byte[] raw, int w = W, int h = H) =>
        Image.FromPixels<Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(raw), w, h);

    internal static byte[] Bytes(Image img)
    {
        Rgba32[] px = img.ToArray<Rgba32>();
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(px).ToArray();
    }

    private static byte[] Sharp(byte[] raw, Action<IImageProcessingContext> op, int w = W, int h = H)
    {
        using SixLabors.ImageSharp.Image<IsRgba32> img = IsImage.LoadPixelData<IsRgba32>(raw, w, h);
        img.Mutate(op);
        byte[] result = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(result);
        return result;
    }

    private static (int Max, double Mean, double Within1) Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        long sum = 0;
        int small = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            sum += d;
            if (d <= 1)
            {
                small++;
            }
        }

        return (max, (double)sum / a.Length, (double)small / a.Length);
    }

    private static void Check(string name, byte[] ours, byte[] theirs, int maxAllowed, double meanAllowed, double minWithin1 = 0)
    {
        (int max, double mean, double within1) = Diff(ours, theirs);
        int at = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            if (Math.Abs(ours[i] - theirs[i]) == max)
            {
                at = i;
                break;
            }
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"{name}: max {max} (at x {(at / 4) % W}, y {(at / 4) / W}, ch {at % 4}: ours {ours[at]}, theirs {theirs[at]}), mean {mean:F4}, within 1 LSB {100 * within1:F2}% [Vector<float>.Count {System.Numerics.Vector<float>.Count}, Avx2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}]");
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_EFFECTS_REPORT");
        if (path != null)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }

        Assert.True(max <= maxAllowed && mean <= meanAllowed && within1 >= minWithin1, $"{line} (allowed max {maxAllowed}, mean {meanAllowed}, within 1 LSB at least {minWithin1})");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(40)]
    public void PixelateMatchesImageSharpExactly(int size)
    {
        byte[] raw = Picture(W, H, 1);
        Check($"pixelate {size}", Bytes(Ours(raw).Pixelate(size)), Sharp(raw, c => c.Pixelate(size)), 0, 0);
    }

    [Fact]
    public void VignetteAndGlowMatchImageSharp()
    {
        byte[] raw = Picture(W, H, 2);
        Check("vignette default", Bytes(Ours(raw).Vignette()), Sharp(raw, c => c.Vignette()), 1, 0.1);
        Check("vignette red 60x30", Bytes(Ours(raw).Vignette(new Rgba32(255, 0, 0, 255), 60, 30)), Sharp(raw, c => c.Vignette(IsColor.Red, 60f, 30f, new IsRect(0, 0, W, H))), 1, 0.1);
        Check("vignette half alpha", Bytes(Ours(raw).Vignette(new Rgba32(0, 0, 0, 128))), Sharp(raw, c => c.Vignette(IsColor.FromRgba(0, 0, 0, 128))), 1, 0.1);
        Check("vignette area", Bytes(Ours(raw).Vignette(area: new ImageRect(20, 10, 120, 100))), Sharp(raw, c => c.Vignette(IsColor.Black, 0f, 0f, new IsRect(20, 10, 120, 100))), 1, 0.1);
        Check("glow default", Bytes(Ours(raw).Glow()), Sharp(raw, c => c.Glow()), 1, 0.1);
        Check("glow white r40", Bytes(Ours(raw).Glow(new Rgba32(255, 255, 255, 255), 40)), Sharp(raw, c => c.Glow(IsColor.White, 40f, new IsRect(0, 0, W, H))), 1, 0.1);
        Check("glow area", Bytes(Ours(raw).Glow(new Rgba32(255, 200, 0, 255), 0, new ImageRect(30, 20, 100, 90))), Sharp(raw, c => c.Glow(IsColor.FromRgba(255, 200, 0, 255), 0f, new IsRect(30, 20, 100, 90))), 1, 0.1);
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(3.0)]
    [InlineData(6.0)]
    public void GaussianSharpenMatchesImageSharp(double sigma)
    {
        byte[] raw = Picture(W, H, 3);
        Check($"gaussian sharpen {sigma}", Bytes(Ours(raw).GaussianSharpen(sigma)), Sharp(raw, c => c.GaussianSharpen((float)sigma)), 2, 0.4);
    }

    private static Action<IImageProcessingContext> SharpEdges(EdgeDetector d, bool gray) => d switch
    {
        EdgeDetector.Sobel => c => c.DetectEdges(KnownEdgeDetectorKernels.Sobel, gray),
        EdgeDetector.Prewitt => c => c.DetectEdges(KnownEdgeDetectorKernels.Prewitt, gray),
        EdgeDetector.Scharr => c => c.DetectEdges(KnownEdgeDetectorKernels.Scharr, gray),
        EdgeDetector.RobertsCross => c => c.DetectEdges(KnownEdgeDetectorKernels.RobertsCross, gray),
        EdgeDetector.Kayyali => c => c.DetectEdges(KnownEdgeDetectorKernels.Kayyali, gray),
        EdgeDetector.Laplacian3x3 => c => c.DetectEdges(KnownEdgeDetectorKernels.Laplacian3x3, gray),
        EdgeDetector.Laplacian5x5 => c => c.DetectEdges(KnownEdgeDetectorKernels.Laplacian5x5, gray),
        EdgeDetector.LaplacianOfGaussian => c => c.DetectEdges(KnownEdgeDetectorKernels.LaplacianOfGaussian, gray),
        EdgeDetector.Kirsch => c => c.DetectEdges(KnownEdgeDetectorKernels.Kirsch, gray),
        _ => c => c.DetectEdges(KnownEdgeDetectorKernels.Robinson, gray),
    };

    [Theory]
    [InlineData(EdgeDetector.Sobel, true)]
    [InlineData(EdgeDetector.Sobel, false)]
    [InlineData(EdgeDetector.Prewitt, true)]
    [InlineData(EdgeDetector.Prewitt, false)]
    [InlineData(EdgeDetector.Scharr, true)]
    [InlineData(EdgeDetector.Scharr, false)]
    [InlineData(EdgeDetector.RobertsCross, true)]
    [InlineData(EdgeDetector.RobertsCross, false)]
    [InlineData(EdgeDetector.Kayyali, true)]
    [InlineData(EdgeDetector.Kayyali, false)]
    [InlineData(EdgeDetector.Laplacian3x3, true)]
    [InlineData(EdgeDetector.Laplacian3x3, false)]
    [InlineData(EdgeDetector.Laplacian5x5, true)]
    [InlineData(EdgeDetector.Laplacian5x5, false)]
    [InlineData(EdgeDetector.LaplacianOfGaussian, true)]
    [InlineData(EdgeDetector.LaplacianOfGaussian, false)]
    [InlineData(EdgeDetector.Kirsch, true)]
    [InlineData(EdgeDetector.Kirsch, false)]
    [InlineData(EdgeDetector.Robinson, true)]
    [InlineData(EdgeDetector.Robinson, false)]
    public void EdgeDetectorsMatchImageSharp(EdgeDetector detector, bool grayscale)
    {
        byte[] raw = Picture(W, H, 4);
        byte[] ours = Bytes(Ours(raw).DetectEdges(detector, grayscale).ConvertTo(PixelFormat.Rgba8));
        Check($"edges {detector} gray={grayscale}", ours, Sharp(raw, SharpEdges(detector, grayscale)), 1, 0.05);
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(20, 9)]
    [InlineData(8, 15)]
    // The two differ only where two intensity bins hold the same number of pixels: this implementation takes the brighter bin,
    // ImageSharp the one that reached the count first while scanning the window, so ties (rare in a window of 64 or more pixels,
    // common in a 25-pixel one) give a different color.
    public void OilPaintWithImageSharpsWindowMatchesImageSharp(int levels, int brush)
    {
        byte[] raw = Picture(W, H, 5);
        Check($"oil {levels}/{brush} (ImageSharp window)", Bytes(Ours(raw).OilPaintAsImageSharp(levels, brush)), Sharp(raw, c => c.OilPaint(levels, brush)), 255, 1.0, 0.96);
    }

    // With gamma 1 the kernels agree with ImageSharp's to rounding. With a higher gamma, pixels that are nearly black in one
    // channel come out up to 20 levels apart, because the cube root turns the 1e-4 float cancellation noise of the two
    // implementations into visible levels there; everywhere else they stay within 1 level.
    [Theory]
    [InlineData(1, 8, 1.0)]
    [InlineData(2, 8, 1.0)]
    [InlineData(3, 8, 1.0)]
    [InlineData(4, 8, 1.0)]
    [InlineData(6, 8, 1.0)]
    [InlineData(2, 8, 2.0)]
    [InlineData(2, 8, 3.0)]
    [InlineData(2, 16, 3.0)]
    [InlineData(3, 12, 3.0)]
    [InlineData(4, 12, 3.0)]
    [InlineData(5, 12, 2.0)]
    [InlineData(6, 12, 3.0)]
    public void BokehBlurIsCloseToImageSharp(int components, int radius, double gamma)
    {
        byte[] raw = Picture(W, H, 6);
        Check($"bokeh c={components} r={radius} g={gamma}", Bytes(Ours(raw).BokehBlur(radius, components, gamma)), Sharp(raw, c => c.BokehBlur(radius, components, (float)gamma)), gamma <= 1 ? 1 : 24, gamma <= 1 ? 0.01 : 0.5, gamma <= 1 ? 1.0 : 0.95);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.2f)]
    [InlineData(0.8f)]
    public void EntropyCropRectangleMatchesImageSharp(float threshold)
    {
        // A flat picture with a textured rectangle in it.
        byte[] raw = new byte[W * H * 4];
        var rnd = new Random(7);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                bool inside = x is >= 50 and < 160 && y is >= 30 and < 120;
                byte v = inside ? (byte)rnd.Next(0, 256) : (byte)90;
                raw[i] = v;
                raw[i + 1] = inside ? (byte)rnd.Next(0, 256) : (byte)90;
                raw[i + 2] = v;
                raw[i + 3] = 255;
            }
        }

        Image ours = Ours(raw).EntropyCrop(threshold);
        using SixLabors.ImageSharp.Image<IsRgba32> sharp = IsImage.LoadPixelData<IsRgba32>(raw, W, H);
        sharp.Mutate(c => c.EntropyCrop(threshold));
        Assert.Equal((sharp.Width, sharp.Height), (ours.Width, ours.Height));
        Assert.Equal(Bytes(ours).Length, sharp.Width * sharp.Height * 4);
    }
}
