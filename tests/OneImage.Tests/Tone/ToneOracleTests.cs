using NetVips;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using static OneImage.Tests.Tone.ToneUtil;
using IsImage = SixLabors.ImageSharp.Image;
using VipsImage = NetVips.Image;

namespace OneImage.Tests.Tone;

/// <summary>
/// Compares the tone operations with ImageSharp and libvips (through NetVips) where the semantics are shared, and records the
/// differences. Where the libraries define an operation differently the test only pins the difference we documented.
/// Set ONEIMAGE_TONE_REPORT to a file path to append the measured numbers.
/// </summary>
public class ToneOracleTests
{
    private const int W = 256;
    private const int H = 64;

    private static byte[] Rgb(int seed = 11)
    {
        var rnd = new Random(seed);
        byte[] raw = new byte[W * H * 3];
        rnd.NextBytes(raw);
        for (int i = 0; i < 256; i++)
        {
            raw[(i * 3)] = (byte)i;
            raw[(i * 3) + 1] = (byte)(255 - i);
            raw[(i * 3) + 2] = (byte)((i * 37) & 255);
        }

        return raw;
    }

    private static Image Ours(byte[] raw) => Image.FromPixels<Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgb24>(raw), W, H);

    private static byte[] OursBytes(Image img) => System.Runtime.InteropServices.MemoryMarshal.AsBytes<OneImage.Rgb24>(img.ToArray<OneImage.Rgb24>()).ToArray();

    private static byte[] Sharp(byte[] raw, Action<IImageProcessingContext> op)
    {
        using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> img = IsImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgb24>(raw, W, H);
        img.Mutate(op);
        byte[] outBytes = new byte[raw.Length];
        img.CopyPixelDataTo(outBytes);
        return outBytes;
    }

    private static byte[] Vips(byte[] raw, Func<VipsImage, VipsImage> op)
    {
        using VipsImage src = VipsImage.NewFromMemory(raw, W, H, 3, Enums.BandFormat.Uchar);
        using VipsImage r = op(src);
        return r.WriteToMemory<byte>();
    }

    private static (int max, double mean) Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            sum += d;
        }

        return (max, (double)sum / a.Length);
    }

    private static void Report(string name, (int max, double mean) d)
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_TONE_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, $"{name}: max {d.max}, mean {d.mean:F3}\n");
        }
    }

    [Fact]
    public void InvertIsExactlyTheSame()
    {
        byte[] raw = Rgb();
        (int max, double mean) a = Diff(OursBytes(Ours(raw).Invert()), Sharp(raw, c => c.Invert()));
        (int max, double mean) b = Diff(OursBytes(Ours(raw).Invert()), Vips(raw, v => v.Invert()));
        Report("invert vs ImageSharp", a);
        Report("invert vs libvips", b);
        Assert.Equal(0, a.max);
        Assert.Equal(0, b.max);
    }

    [Fact]
    public void GrayscaleAgreesWhereTheMathIsTheSame()
    {
        byte[] raw = Rgb();
        // ImageSharp weighs stored values with Rec. 709: our linear:false.
        (int max, double mean) sharp = Diff(OursBytes(Ours(raw).Grayscale(linear: false)), Sharp(raw, c => c.Grayscale(GrayscaleMode.Bt709)));
        Report("grayscale(linear:false) vs ImageSharp Bt709", sharp);
        Assert.True(sharp.max <= 1, $"max {sharp.max}");

        // libvips B_W converts through XYZ in linear light: our default.
        (int max, double mean) vips = Diff(OursBytes(Ours(raw).Grayscale()), Vips(raw, v => v.Colourspace(Enums.Interpretation.Bw).Bandjoin([v.Colourspace(Enums.Interpretation.Bw), v.Colourspace(Enums.Interpretation.Bw)])));
        Report("grayscale(default, linear) vs libvips b-w", vips);
        Assert.True(vips.max <= 2, $"max {vips.max}");
    }

    [Fact]
    public void GammaAgreesWithLibvips()
    {
        byte[] raw = Rgb();
        // libvips gamma(e) = (in/255)^(1/e) * 255, the same as ours.
        (int max, double mean) d = Diff(OursBytes(Ours(raw).Gamma(2.2)), Vips(raw, v => v.Gamma(exponent: 2.2)));
        Report("gamma(2.2) vs libvips gamma", d);
        Assert.True(d.max <= 1, $"max {d.max}");
    }

    [Fact]
    public void BrightnessContrastAgreeWithTheEquivalentLinearMaps()
    {
        byte[] raw = Rgb();
        // ImageSharp Contrast(k) scales around mid-gray: ours with k = tan((c+1)pi/4).
        double k = 1.5;
        double c = (4 * Math.Atan(k) / Math.PI) - 1;
        (int max, double mean) sharpContrast = Diff(OursBytes(Ours(raw).BrightnessContrast(0, c)), Sharp(raw, x => x.Contrast((float)k)));
        Report("contrast (k=1.5) vs ImageSharp Contrast", sharpContrast);
        Assert.True(sharpContrast.max <= 1, $"max {sharpContrast.max}");

        // ImageSharp Brightness(m) multiplies; ours Exposure in encoded space with stops = log2(m) is the same map.
        (int max, double mean) sharpBright = Diff(OursBytes(Ours(raw).Exposure(Math.Log2(1.2), linear: false)), Sharp(raw, x => x.Brightness(1.2f)));
        Report("brightness x1.2 vs ImageSharp Brightness", sharpBright);
        Assert.True(sharpBright.max <= 1, $"max {sharpBright.max}");

        // libvips linear(a, b): out = a*in + b (its cast to 8 bits truncates, so half a step is added to compare rounded results), which is brightness/contrast with a = k and b = 255*(0.5 - 0.5k + brightness).
        double bright = 0.1;
        (int max, double mean) vips = Diff(OursBytes(Ours(raw).BrightnessContrast(bright, c)), Vips(raw, v => v.Linear([k], [(255 * (0.5 - (0.5 * k) + bright)) + 0.5]).Cast(Enums.BandFormat.Uchar)));
        Report("brightness/contrast vs libvips linear", vips);
        Assert.True(vips.max <= 1, $"max {vips.max}");
    }

    [Fact]
    public void LevelsAgreeWithLibvipsLinearAndClip()
    {
        byte[] raw = Rgb();
        double ib = 20 / 255.0, iw = 230 / 255.0;
        double a = 1 / (iw - ib);
        (int max, double mean) d = Diff(OursBytes(Ours(raw).Levels(ib, iw)), Vips(raw, v => v.Linear([a], [(-255 * ib * a) + 0.5]).Cast(Enums.BandFormat.Uchar)));
        Report("levels (no gamma) vs libvips linear+cast", d);
        Assert.True(d.max <= 1, $"max {d.max}");
    }

    [Fact]
    public void HueAndSaturationDifferFromTheMatrixModels()
    {
        byte[] raw = Rgb();
        // ImageSharp saturate/hue are luminance-preserving matrices; ours is HSL. Full desaturation is the clearest case:
        // HSL gives (max+min)/2, the matrix gives Rec. 709 luma. Document the size of the difference.
        (int max, double mean) sat = Diff(OursBytes(Ours(raw).HueSaturationLightness(saturation: -1)), Sharp(raw, x => x.Saturate(0f)));
        Report("saturation -1 vs ImageSharp Saturate(0) (different models)", sat);
        (int max, double mean) hue = Diff(OursBytes(Ours(raw).HueSaturationLightness(hue: 90)), Sharp(raw, x => x.Hue(90f)));
        Report("hue 90 vs ImageSharp Hue(90) (different models)", hue);
        Assert.True(sat.max > 1);

        // The HSL desaturation is exactly the mid-range gray (max+min)/2, whatever the library does.
        byte[] ours = OursBytes(Ours(raw).HueSaturationLightness(saturation: -1));
        for (int i = 0; i < raw.Length; i += 3)
        {
            int mx = Math.Max(raw[i], Math.Max(raw[i + 1], raw[i + 2]));
            int mn = Math.Min(raw[i], Math.Min(raw[i + 1], raw[i + 2]));
            Assert.InRange(Math.Abs(ours[i] - ((mx + mn) / 2.0)), 0, 1);
        }
    }
}
