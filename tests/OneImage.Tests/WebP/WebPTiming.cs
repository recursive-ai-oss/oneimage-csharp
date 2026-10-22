using System.Diagnostics;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>Opt-in timing of the raw decoders and encoders, without the image pipeline (build with -c Release). Set ONEIMAGE_WEBP_REPORT.</summary>
public sealed class WebPTiming
{
    private sealed class NullSink(int w, int h, PixelFormat f) : IRowSink
    {
        public int Width => w;

        public int Height => h;

        public PixelFormat Format => f;

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
        }
    }

    private static double Time(Action a, int n)
    {
        // Minimum over the runs: the host is shared, so the fastest run is the least disturbed one.
        a();
        double best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    [Fact]
    public void RawDecodeAndEncodeTimes()
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_REPORT");
        Assert.SkipUnless(path != null, "report only");
        var lines = new List<string>();
        (string name, byte[] rgba, int w, int h) = WebPCorpus.Photos(large: true).First(p => p.Name == "plasma-large");
        var fmt = WebPFormat.Instance;
        foreach (bool lossless in new[] { false, true })
        {
            byte[] file = WebPOracle.SkiaEncode(rgba, w, h, lossless, 75);
            double libwebp = Time(() => WebPOracle.SkiaDecode(file, out _, out _), 10);
            double decodeOnly = Time(() =>
            {
                using var ms = new MemoryStream(file);
                ImageInfo info = fmt.Decoder!.ReadInfo(ms, new ImageLimits());
                fmt.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, w, h)), new NullSink(w, h, info.Frames[0].Format), new ImageLimits(), CancellationToken.None);
            }, 10);
            double viaImage = Time(() =>
            {
                Image img = Image.Load(file);
                if (img.Format == PixelFormat.Rgb8) { _ = img.ToArray<Rgb24>(); } else { _ = img.ToArray<Rgba32>(); }
            }, 10);
            double viaRgba = Time(() => { Image img = Image.Load(file); _ = img.ToArray<Rgba32>(); }, 10);
            lines.Add($"{(lossless ? "lossless" : "lossy"),-9} decode {w}x{h}: libwebp(skia) {libwebp:F1} ms, ours raw {decodeOnly:F1} ms, ours via Image (native format) {viaImage:F1} ms, via Image as Rgba32 {viaRgba:F1} ms");
        }

        Image src = Image.FromPixels<Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(rgba), w, h);
        foreach (bool lossless in new[] { false, true })
        {
            double theirs = Time(() => WebPOracle.SkiaEncode(rgba, w, h, lossless, 75), 3);
            int size = 0;
            double ours = Time(() =>
            {
                using var ms = new MemoryStream();
                src.Save(ms, fmt, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless } });
                size = (int)ms.Length;
            }, 3);
            lines.Add($"{(lossless ? "lossless" : "lossy"),-9} encode {w}x{h}: libwebp {theirs:F0} ms, ours {ours:F0} ms (size {size} vs {WebPOracle.SkiaEncode(rgba, w, h, lossless, 75).Length})");
        }

        File.AppendAllLines(path!, lines);
    }

    // For profilers: ONEIMAGE_WEBP_SPIN=lossy-decode|lossless-decode|lossy-encode|lossless-encode loops the raw operation.
    [Fact]
    public void Spin()
    {
        string? mode = Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_SPIN");
        Assert.SkipUnless(mode != null, "profiling only");
        (_, byte[] rgba, int w, int h) = WebPCorpus.Photos(large: true).First(p => p.Name == "plasma-large");
        var fmt = WebPFormat.Instance;
        bool lossless = mode!.StartsWith("lossless", StringComparison.Ordinal);
        byte[] file = WebPOracle.SkiaEncode(rgba, w, h, lossless, 75);
        Image src = Image.FromPixels<Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(rgba), w, h);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 25)
        {
            if (mode.EndsWith("decode", StringComparison.Ordinal))
            {
                using var ms = new MemoryStream(file);
                ImageInfo info = fmt.Decoder!.ReadInfo(ms, new ImageLimits());
                fmt.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, w, h)), new NullSink(w, h, info.Frames[0].Format), new ImageLimits(), CancellationToken.None);
            }
            else
            {
                using var ms = new MemoryStream();
                src.Save(ms, fmt, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless } });
            }
        }
    }
}
