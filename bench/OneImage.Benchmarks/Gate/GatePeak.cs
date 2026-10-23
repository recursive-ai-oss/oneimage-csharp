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
/// benchmark methods), runs the operation once to warm up, collects garbage, resets the kernel's high-water mark for this
/// process (<c>clear_refs</c> 5), runs the operation <c>count</c> times and prints one <c>PEAK</c> line: resident set before the
/// run, peak resident set during it, and the mean time. The difference is the working memory of the operation itself, without
/// the inputs the setup built and without one-time costs (code generation, thread-pool start-up, first native library load)
/// that a long-running process pays once. Every library gets the same warm-up.
/// </summary>
internal static class GatePeak
{
    public static readonly string[] Libraries = ["oneimage", "imagesharp", "skia", "netvips", "magick"];

    public static readonly string[] Tasks =
    [
        "thumb-jpeg", "thumb-webp", "png-to-webp", "jpeg-decode", "jpeg-decode-rgba", "jpeg-encode", "png-decode", "png-encode",
        "webp-decode", "webp-encode", "gif-decode", "gif-encode", "gif-resize", "tiff-multipage-decode", "tiff-encode",
        "bmp-decode", "bmp-encode", "upscale-2x", "watermark", "cmyk-jpeg-to-srgb",
    ];

    // PNG tasks default to the noisy photo input; GATE_PNG_CONTENT=natural|screenshot measures the others.
    private static readonly string PngContent = Environment.GetEnvironmentVariable("GATE_PNG_CONTENT") ?? "photo";

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

        // One warm-up is the gate's method. GATE_WARMUPS=N runs N instead, for checking how much of a row's peak is the one-time
        // code generation of the runtime's tiered JIT (hot methods are recompiled after 30 calls) rather than the operation itself.
        int warmups = int.TryParse(Environment.GetEnvironmentVariable("GATE_WARMUPS"), out int wu) && wu > 0 ? wu : 1;
        for (int i = 0; i < warmups; i++)
        {
            op();
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        File.WriteAllText("/proc/self/clear_refs", "5");
        long before = Status("VmRSS:");
        var sw = new Stopwatch();
        for (int i = 0; i < count; i++)
        {
            sw.Start();
            op();
            sw.Stop();

            // The result of one run is dead once the call returns. Without a collection here, managed results (a 24 MP array
            // is on the large object heap) pile up until the collector decides to run, and the peak would measure that
            // timing instead of one operation's working memory. Native and pooled libraries are unaffected. The collection
            // is outside the timed section.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
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
                var g = new PngGateBenchmarks { Content = PngContent };
                g.Setup();
                return new() { ["oneimage"] = () => g.PngToWebPOneImage(), ["skia"] = () => g.PngToWebPSkia(), ["imagesharp"] = () => g.PngToWebPImageSharp(), ["netvips"] = () => g.PngToWebPNetVips(), ["magick"] = () => g.PngToWebPMagick() };
            }

            case "jpeg-decode":
            {
                var b = new JpegDecodeBenchmarks();
                b.Setup();
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageFull(), ["oneimage-alloc"] = () => b.OneImageFullToArray(), ["skia"] = () => b.SkiaFull(), ["imagesharp"] = () => b.ImageSharpFull(), ["netvips"] = () => b.NetVipsFull(), ["magick"] = () => g.MagickDecode() };
            }

            case "jpeg-decode-rgba":
            {
                var g = new JpegGateBenchmarks();
                g.Setup();
                return new() { ["oneimage"] = () => g.OneImageRgba(), ["oneimage-alloc"] = () => g.OneImageRgbaToArray(), ["skia"] = () => g.SkiaRgba(), ["imagesharp"] = () => g.ImageSharpRgba() };
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
                var b = new PngDecodeBenchmarks { Content = PngContent };
                b.Setup();
                var g = new PngGateBenchmarks { Content = PngContent };
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageDecode(), ["skia"] = () => b.SkiaDecode(), ["imagesharp"] = () => b.ImageSharpDecode(), ["netvips"] = () => b.NetVipsDecode(), ["magick"] = () => g.MagickDecode() };
            }

            case "png-encode":
            {
                var b = new PngEncodeBenchmarks { Content = PngContent };
                b.Setup();
                var g = new PngGateBenchmarks { Content = PngContent };
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

            case "webp-encode-24mp":
            {
                var b = new WebPEncodeBenchmarks { Case = "lossy-6000x4000" };
                b.Setup();
                var g = new WebPEncodeGateBenchmarks { Case = "lossy-6000x4000" };
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageLib(), ["skia"] = () => b.SkiaSharp(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickEncode() };
            }

            case "webp-encode-lossless":
            {
                var b = new WebPEncodeBenchmarks { Case = "lossless-2000x1500" };
                b.Setup();
                var g = new WebPEncodeGateBenchmarks { Case = "lossless-2000x1500" };
                g.Setup();
                return new() { ["oneimage"] = () => b.OneImageLib(), ["skia"] = () => b.SkiaSharp(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => g.MagickEncode() };
            }

            case "webp-encode-lossless-24mp":
            {
                var b = new WebPEncodeBenchmarks { Case = "lossless-6000x4000" };
                b.Setup();
                var g = new WebPEncodeGateBenchmarks { Case = "lossless-6000x4000" };
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
                return new() { ["oneimage"] = () => b.OneImage(), ["oneimage-alloc"] = () => b.OneImageToArray(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVips(), ["magick"] = () => b.Magick() };
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
                return new() { ["oneimage"] = () => b.OursLinearIntoBuffer(), ["skia"] = () => b.SkiaSharp(), ["imagesharp"] = () => b.ImageSharp(), ["netvips"] = () => b.NetVipsResize(), ["magick"] = () => g.MagickUpscale() };
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
                return new() { ["oneimage"] = () => b.OneImageCmyk(), ["oneimage-alloc"] = () => b.OneImageCmykToArray(), ["skia"] = () => b.SkiaCmyk(), ["imagesharp"] = () => b.ImageSharpCmyk(), ["netvips"] = () => b.NetVipsCmyk(), ["magick"] = () => b.MagickCmyk() };
            }

            default:
                throw new ArgumentException("unknown gate task " + task);
        }
    }
}
