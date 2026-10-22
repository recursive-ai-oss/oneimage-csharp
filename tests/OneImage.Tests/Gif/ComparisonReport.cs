using ImageMagick;
using OneImage.Benchmarks.Gif;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using IS = SixLabors.ImageSharp;
using Rgba32Is = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Tests.Gif;

/// <summary>
/// Prints file sizes and quantizer PSNR next to ImageSharp and Magick.NET. It only runs when ONEIMAGE_REPORT=1,
/// and writes to the file named by ONEIMAGE_REPORT_FILE (or the console).
/// </summary>
public class ComparisonReport
{
    [Fact]
    public void Report()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEIMAGE_REPORT") == "1", "set ONEIMAGE_REPORT=1 to run");
        var lines = new List<string>();

        // Animation sizes
        byte[] input = GifBenchData.Animation();
        Image ours = Image.Load(input, new LoadOptions { Validate = true });
        lines.Add($"animation: {ours.FrameCount} frames {ours.Width}x{ours.Height}");
        lines.Add($"  input (Magick optimized): {input.Length} bytes");
        lines.Add($"  oneimage, differencing:   {GifTestUtil.Encode(ours).Length} bytes");
        lines.Add($"  oneimage, full frames:    {GifTestUtil.Encode(ours, new GifEncoderOptions { FrameDifferencing = false }).Length} bytes");
        using (IS.Image<Rgba32Is> sharp = IS.Image.Load<Rgba32Is>(input))
        using (var ms = new MemoryStream())
        {
            sharp.Save(ms, new GifEncoder());
            lines.Add($"  ImageSharp:               {ms.Length} bytes");
        }

        using (var coll = new MagickImageCollection(input))
        using (var ms = new MemoryStream())
        {
            coll.Coalesce();
            coll.Write(ms, MagickFormat.Gif);
            lines.Add($"  Magick.NET (coalesced):   {ms.Length} bytes");
            using var opt = new MagickImageCollection(ms.ToArray());
            opt.Coalesce();
            opt.Optimize();
            using var ms2 = new MemoryStream();
            opt.Write(ms2, MagickFormat.Gif);
            lines.Add($"  Magick.NET (Optimize()):  {ms2.Length} bytes");
        }

        // Exact decode check of our own output
        Image back = GifTestUtil.Decode(GifTestUtil.Encode(ours));
        int bad = 0;
        for (int i = 0; i < ours.FrameCount; i++)
        {
            bad += GifTestUtil.SameVisible(GifTestUtil.Pixels(ours, i), GifTestUtil.Pixels(back, i)) ? 0 : 1;
        }

        lines.Add($"  frames that differ after re-encode: {bad}");

        // Quantizer PSNR on photos
        foreach (string photo in Photos())
        {
            byte[] rgba = GifBenchData.PhotoRgba(out int w, out int h, photo);
            var px = new Rgba32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Rgba32(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2], 255);
            }

            Image img = Image.FromPixels<Rgba32>(px, w, h);
            lines.Add($"photo {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(photo)))} {w}x{h}, 256 colors, PSNR over RGB:");
            lines.Add($"  oneimage no dither:      {GifTestUtil.Psnr(px, img.Quantize(256).ToArray<Rgba32>()):F2} dB");
            lines.Add($"  oneimage Floyd-Steinberg:{GifTestUtil.Psnr(px, img.Quantize(256, Dither.FloydSteinberg).ToArray<Rgba32>()):F2} dB");
            lines.Add($"  oneimage ordered:        {GifTestUtil.Psnr(px, img.Quantize(256, Dither.Ordered).ToArray<Rgba32>()):F2} dB");
            using (IS.Image<Rgba32Is> s = IS.Image.LoadPixelData<Rgba32Is>(rgba, w, h))
            {
                s.Mutate(x => x.Quantize(new WuQuantizer(new QuantizerOptions { MaxColors = 256, Dither = null })));
                lines.Add($"  ImageSharp Wu no dither: {Psnr(px, s):F2} dB");
            }

            using (IS.Image<Rgba32Is> s = IS.Image.LoadPixelData<Rgba32Is>(rgba, w, h))
            {
                s.Mutate(x => x.Quantize(new WuQuantizer(new QuantizerOptions { MaxColors = 256 })));
                lines.Add($"  ImageSharp Wu + its default dither: {Psnr(px, s):F2} dB");
            }

            using (var m = new MagickImage(MagickColors.Black, (uint)w, (uint)h))
            {
                m.ImportPixels(rgba, new PixelImportSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));
                m.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.No });
                byte[] q = m.GetPixels().ToByteArray(PixelMapping.RGBA)!;
                var qp = new Rgba32[w * h];
                for (int i = 0; i < qp.Length; i++)
                {
                    qp[i] = new Rgba32(q[i * 4], q[(i * 4) + 1], q[(i * 4) + 2], 255);
                }

                lines.Add($"  Magick.NET no dither:    {GifTestUtil.Psnr(px, qp):F2} dB");
            }

            lines.Add($"  GIF size, oneimage default (FS): {GifTestUtil.Encode(img).Length} bytes");
            using var si = IS.Image.LoadPixelData<Rgba32Is>(rgba, w, h);
            using var sms = new MemoryStream();
            si.Save(sms, new GifEncoder());
            lines.Add($"  GIF size, ImageSharp default:    {sms.Length} bytes");
        }

        string? file = Environment.GetEnvironmentVariable("ONEIMAGE_REPORT_FILE");
        if (file != null)
        {
            File.WriteAllLines(file, lines);
        }
        else
        {
            Console.WriteLine(string.Join('\n', lines));
        }
    }

    private static double Psnr(Rgba32[] src, IS.Image<Rgba32Is> q)
    {
        var px = new Rgba32[src.Length];
        int w = q.Width;
        for (int y = 0; y < q.Height; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32Is p = q[x, y];
                px[(y * w) + x] = new Rgba32(p.R, p.G, p.B, 255);
            }
        }

        return GifTestUtil.Psnr(src, px);
    }

    private static IEnumerable<string> Photos()
    {
        string? one = Environment.GetEnvironmentVariable("ONEIMAGE_PHOTO");
        if (one != null)
        {
            yield return one;
            yield break;
        }

        if (Directory.Exists("/usr/share/wallpapers"))
        {
            foreach (string p in Directory.EnumerateFiles("/usr/share/wallpapers", "2560x1600.jpg", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Take(4))
            {
                yield return p;
            }
        }
    }
}
