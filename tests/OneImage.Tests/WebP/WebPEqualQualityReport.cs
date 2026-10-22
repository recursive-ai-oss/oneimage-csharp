using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// Size of our lossy output against libwebp's at equal quality: both encoders run over a grid of quality settings, and for
/// every point of ours the size libwebp needs for the same SSIM (or PSNR) is read off its own curve. Run only when
/// ONEIMAGE_WEBP_REPORT names an output file.
/// </summary>
public sealed class WebPEqualQualityReport
{
    private static readonly int[] Grid = [20, 30, 40, 50, 60, 70, 75, 80, 85, 90, 95];

    private static IEnumerable<(string Name, byte[] Rgba, int W, int H)> Images()
    {
        foreach ((string name, byte[] rgba, int w, int h) in WebPCorpus.Photos())
        {
            yield return (name, rgba, w, h);
        }

        yield return Scene("scene-a", 512, 384, 21, 1.6);
        yield return Scene("scene-b", 640, 480, 22, 0.8);
        yield return Granite();
    }

    // A photo-like picture: soft plasma color fields, a few hard-edged shapes and light sensor noise.
    private static (string, byte[], int, int) Scene(string name, int w, int h, int seed, double blur)
    {
        MagickNET.SetRandomSeed((ulong)seed);
        using var img = new MagickImage("plasma:fractal", (uint)w, (uint)h);
        img.Blur(0, blur);
        var rnd = new Random(seed);
        for (int i = 0; i < 6; i++)
        {
            new Drawables()
                .FillColor(new MagickColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)))
                .StrokeColor(MagickColors.Transparent)
                .Ellipse(rnd.Next(w), rnd.Next(h), 20 + rnd.Next(80), 15 + rnd.Next(60), 0, 360)
                .Draw(img);
        }

        img.Blur(0, 0.7);
        img.AddNoise(NoiseType.Gaussian, 0.15);
        return (name, ToRgba(img), w, h);
    }

    private static (string, byte[], int, int) Granite()
    {
        using var tile = new MagickImage("granite:");
        using var img = new MagickImage(MagickColors.Black, 512, 384);
        img.Tile(tile, CompositeOperator.Over);
        return ("granite", ToRgba(img), 512, 384);
    }

    private static byte[] ToRgba(MagickImage img)
    {
        img.HasAlpha = false;
        using IPixelCollection<byte> px = img.GetPixels();
        byte[] rgb = px.ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException();
        byte[] rgba = new byte[rgb.Length / 3 * 4];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
        {
            rgba[j] = rgb[i];
            rgba[j + 1] = rgb[i + 1];
            rgba[j + 2] = rgb[i + 2];
            rgba[j + 3] = 255;
        }

        return rgba;
    }

    private static (double Ssim, double Psnr, int Size)[] Curve(byte[] rgba, int w, int h, bool ours)
    {
        var points = new (double, double, int)[Grid.Length];
        for (int i = 0; i < Grid.Length; i++)
        {
            byte[] file = ours
                ? WebPLosslessEncodeTests.WrapChunk("VP8 "u8, OneImage.Codecs.WebP.VP8Encoder.Encode(rgba, w, h, Grid[i], 4, CancellationToken.None, 2))
                : WebPOracle.SkiaEncode(rgba, w, h, lossless: false, quality: Grid[i]);
            byte[] dec = WebPOracle.SkiaDecode(file, out _, out _);
            points[i] = (Quality.Ssim(rgba, dec, w, h), Quality.Psnr(rgba, dec), file.Length);
        }

        return points;
    }

    // Mean of log(our size / their size at the same metric value), over our points inside their range.
    private static double MeanLogRatio((double Ssim, double Psnr, int Size)[] ours, (double Ssim, double Psnr, int Size)[] theirs, bool ssim)
    {
        var curve = theirs.Select(p => (m: ssim ? p.Ssim : p.Psnr, ls: Math.Log(p.Size))).OrderBy(p => p.m).ToArray();
        double sum = 0;
        int n = 0;
        foreach (var o in ours)
        {
            double m = ssim ? o.Ssim : o.Psnr;
            if (m < curve[0].m || m > curve[^1].m)
            {
                continue;
            }

            int k = 1;
            while (curve[k].m < m)
            {
                k++;
            }

            double t = (m - curve[k - 1].m) / Math.Max(1e-12, curve[k].m - curve[k - 1].m);
            double interp = curve[k - 1].ls + (t * (curve[k].ls - curve[k - 1].ls));
            sum += Math.Log(o.Size) - interp;
            n++;
        }

        return n == 0 ? 0 : sum / n;
    }

    [Fact]
    public void LossySizeAtEqualQualityVersusLibwebp()
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_REPORT");
        Assert.SkipUnless(path != null, "report only");
        var lines = new List<string>();
        double sumS = 0, sumP = 0;
        int count = 0;
        foreach ((string name, byte[] rgba, int w, int h) in Images())
        {
            var ours = Curve(rgba, w, h, ours: true);
            var theirs = Curve(rgba, w, h, ours: false);
            double s = MeanLogRatio(ours, theirs, ssim: true);
            double p = MeanLogRatio(ours, theirs, ssim: false);
            lines.Add($"{name,-10} size at equal SSIM {(Math.Exp(s) - 1) * 100,+6:F2}%  at equal PSNR {(Math.Exp(p) - 1) * 100,+6:F2}%   q75: ours {ours[6].Size} ssim {ours[6].Ssim:F4} | libwebp {theirs[6].Size} ssim {theirs[6].Ssim:F4}");
            sumS += s;
            sumP += p;
            count++;
        }

        lines.Add($"MEAN       size at equal SSIM {(Math.Exp(sumS / count) - 1) * 100,+6:F2}%  at equal PSNR {(Math.Exp(sumP / count) - 1) * 100,+6:F2}%");
        File.AppendAllLines(path!, lines);
    }
}
