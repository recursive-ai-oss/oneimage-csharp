using System.Diagnostics;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using OneImage.Benchmarks.Jpeg;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgb = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.Composite;

/// <summary>
/// Peak memory of the thumbnail task, one library per process so the numbers do not mix:
/// <c>OneImage.Benchmarks.dll peak gen file.jpg</c> writes the 24 MP test file;
/// <c>peak oneimage|skia|imagesharp|netvips file.jpg [count]</c> makes <c>count</c> thumbnails from the file and prints the peak working set
/// (run it under <c>/usr/bin/time -v</c> for the kernel's maximum RSS).
/// </summary>
internal static class PeakMemoryRunner
{
    public static int Run(string[] args)
    {
        string kind = args[1];
        string file = args[2];
        if (kind == "gen")
        {
            File.WriteAllBytes(file, JpegData.SkiaJpeg(JpegData.Pixels(), 90));
            return 0;
        }

        int count = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 10;
        byte[] jpeg = File.ReadAllBytes(file);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using var ms = new MemoryStream();
            switch (kind)
            {
                case "oneimage":
                    global::OneImage.Image.Thumbnail(jpeg, 400).Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 90 } });
                    break;
                case "skia":
                {
                    using var codec = SKCodec.Create(new MemoryStream(jpeg));
                    SKSizeI size = codec.GetScaledDimensions(1f / 8);
                    using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
                    codec.GetPixels(decoded.Info, decoded.GetPixels());
                    int h = (int)Math.Round(400.0 * codec.Info.Height / codec.Info.Width);
                    using SKBitmap small = decoded.Resize(new SKImageInfo(400, h, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    using var image = SKImage.FromBitmap(small);
                    using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                    ms.Write(data.AsSpan());
                    break;
                }

                case "imagesharp":
                {
                    var options = new DecoderOptions { TargetSize = new Size(400, 267) };
                    using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(options, jpeg);
                    img.Mutate(x => x.Resize(400, 0));
                    img.Save(ms, new JpegEncoder { Quality = 90 });
                    break;
                }

                case "netvips":
                {
                    using VipsImage img = VipsImage.ThumbnailBuffer(jpeg, 400);
                    ms.Write(img.JpegsaveBuffer(q: 90));
                    break;
                }

                default:
                    return 2;
            }
        }

        Console.WriteLine($"{kind}: {count} thumbnails in {sw.Elapsed.TotalMilliseconds / count:F1} ms each, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MiB, managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MiB");
        return 0;
    }
}
