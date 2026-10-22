using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using OneImage.Benchmarks.Composite;
using OneImage.Benchmarks.Gif;
using OneImage.Benchmarks.Jpeg;
using OneImage.Benchmarks.Png;
using OneImage.Benchmarks.Resample;
using OneImage.Benchmarks.Tiff;
using OneImage.Benchmarks.WebP;

namespace OneImage.Benchmarks.Gate;

/// <summary>
/// Peak memory of one gate task for one library, one library per process so the numbers do not mix:
/// <c>gate-peak &lt;task&gt; &lt;library&gt; [count]</c> builds the task's inputs exactly like the timed benchmark does (it calls the same
/// benchmark methods), collects garbage, resets the kernel's high-water mark for this process (<c>clear_refs</c> 5), runs the
/// operation <c>count</c> times and prints one <c>PEAK</c> line: resident set before the run, peak resident set during it, and
/// the mean time. The difference is the working memory of the operation itself, without the inputs the setup built.
/// </summary>
internal static class GatePeak
{
    public static readonly string[] Libraries = ["oneimage", "imagesharp", "skia", "netvips", "magick"];

    public static readonly string[] Tasks =
    [
        "thumb-jpeg", "thumb-webp", "png-to-webp", "jpeg-decode", "jpeg-encode", "png-decode", "png-encode",
        "webp-decode", "webp-encode", "gif-decode", "gif-encode", "gif-resize", "tiff-multipage-decode", "tiff-encode",
        "bmp-decode", "bmp-encode", "upscale-2x", "watermark", "cmyk-jpeg-to-srgb",
    ];

    public static int Run(string[] args)
    {
        string task = args[1];
        string lib = args[2];
        int count = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 4;
        Dictionary<string, Action> ops = Build(task);
        if (!ops.TryGetValue(lib, out Action? op))
        {
            Console.WriteLine($"SKIP {task} {lib}");
            return 0;
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        File.WriteAllText("/proc/self/clear_refs", "5");
        long before = Status("VmRSS:");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            op();
        }

        double ms = sw.Elapsed.TotalMilliseconds / count;
        long peak = Status("VmHWM:");
        Console.WriteLine($"PEAK\t{task}\t{lib}\t{before / 1024.0:F1}\t{peak / 1024.0:F1}\t{ms:F1}");
        return 0;
    }

    private static long Status(string key)
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                return long.Parse(line.AsSpan(key.Length).Trim().TrimEnd(" kB".AsSpan()), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    private static Dictionary<string, Action> Build(string task)
    {
        switch (task)
        {
            case "thumb-jpeg":
            {
                var b = new ThumbnailBenchmarks();
                b.Setup();
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImage(), ["skia"] = () => b.Skia(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickThumbJpeg() };
            }

            case "thumb-webp":
            {
                var b = new ThumbnailWebPBenchmarks();
                b.Setup();
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImage(), ["skia"] = () => b.Skia(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickThumbWebP() };
            }

            case "png-to-webp":
            {
                var g = new PngGateBenchmarks { Content = "photo" };
                g.Setup();
                return new() { ["oneimage"] = () => g.PngToWebPOneImage(), ["skia"] = () => g.PngToWebPSkia(), ["imagesharp"] = () => g.PngToWebPImageSharp(), ["netvips"] = () => g.PngToWebPNetVips(), ["magick"] = () => g.PngToWebPMagick() };
            }

            case "jpeg-decode":
            {
                var b = new JpegDecodeBenchmarks();
                b.Setup();
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageFull(), ["skia"] = () => b.SkiaFull(), ["imagesharp"] = () => b.ImageSharpFull(), ["netvips"] = () => b.NetVipsFull(), ["magick"] = () => g.MagickDecode() };
            }

            case "jpeg-encode":
            {
                var b = new JpegEncodeBenchmarks();
                b.Setup();
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageOptimized(), ["skia"] = () => b.Skia(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.Vips(), ["magick"] = () => g.MagickEncode() };
            }

            case "png-decode":
            {
                var b = new PngDecodeBenchmarks { Content = "photo" };
                b.Setup();
                var g = new PngGateBenchmarks { Content = "photo" };
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageDecode(), ["skia"] = () => b.SkiaDecode(), ["imagesharp"] = () => b.ImageSharpDecode(), ["netvips"] = () => b.NetVipsDecode(), ["magick"] = () => g.MagickDecode() };
            }

            case "png-encode":
            {
                var b = new PngEncodeBenchmarks { Content = "photo" };
                b.Setup();
                var g = new PngGateBenchmarks { Content = "photo" };
                g.Setup();
                return new() { ["oneimage"] = () => b.Ours(), ["skia"] = () => b.Skia(), ["imagesharp"] = () => b.Sharp(), ["netvips"] = () => b.Vips(), ["magick"] = () => g.MagickEncode() };
            }

            case "webp-decode":
            {
                var b = new WebPDecodeBenchmarks { Lossless = false };
                b.Setup();
                var g = new WebPDecodeGateBenchmarks { Lossless = false };
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageLib(), ["skia"] = () => b.SkiaSharp(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickDecode() };
            }

            case "webp-encode":
            {
                var b = new WebPEncodeBenchmarks { Case = "lossy-2000x1500" };
                b.Setup();
                var g = new WebPEncodeGateBenchmarks { Case = "lossy-2000x1500" };
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageLib(), ["skia"] = () => b.SkiaSharp(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickEncode() };
            }

            case "gif-decode":
            {
                var b = new GifDecodeBenchmarks();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImageDecode(), ["skia"] = () => b.SkiaDecode(), ["imagesharp"] = () => b.ImageSharpDecode(), ["magick"] = () => b.MagickDecode() };
            }

            case "gif-encode":
            {
                var b = new GifEncodeBenchmarks();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImageEncode(), ["imagesharp"] = () => b.ImageSharpEncode(), ["magick"] = () => b.MagickEncode() };
            }

            case "gif-resize":
            {
                var b = new GifResizeGateBenchmarks();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImageGifResize(), ["imagesharp"] = () => b.ImageSharpGifResize(), ["netvips"] = () => b.NetVipsGifResize(), ["magick"] = () => b.MagickGifResize() };
            }

            case "tiff-multipage-decode":
            {
                var b = new TiffMultiPageDecode();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImage(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => b.Magick() };
            }

            case "tiff-encode":
            {
                var b = new TiffLzwEncode();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImagePredictor(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => b.Magick() };
            }

            case "bmp-decode":
            {
                var b = new BmpGateBenchmarks();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImageDecode(), ["skia"] = () => b.SkiaDecode(), ["imagesharp"] = () => b.ImageSharpDecode(), ["magick"] = () => b.MagickDecode() };
            }

            case "bmp-encode":
            {
                var b = new BmpGateBenchmarks();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImageEncode(), ["imagesharp"] = () => b.ImageSharpEncode(), ["magick"] = () => b.MagickEncode() };
            }

            case "upscale-2x":
            {
                var b = new UpscaleBenchmarks();
                b.Setup();
                var g = new UpscaleGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OursLinear(), ["skia"] = () => b.SkiaSharp(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVipsResize(), ["magick"] = () => g.MagickUpscale() };
            }

            case "watermark":
            {
                var b = new WatermarkBenchmarks();
                b.Setup();
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImage(), ["skia"] = () => b.Skia(), ["imagesharp"] = () => b.ImageSharpPacked(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickWatermark() };
            }

            case "cmyk-jpeg-to-srgb":
            {
                var b = new CmykJpegGateBenchmarks();
                b.Setup();
                return new() { ["oneimage"] = () => b.OneImageCmyk(), ["skia"] = () => b.SkiaCmyk(), ["imagesharp"] = () => b.ImageSharpCmyk(), ["netvips"] = () => b.NetVipsCmyk(), ["magick"] = () => b.MagickCmyk() };
            }

            default:
                throw new ArgumentException("unknown gate task " + task);
        }
    }
}
