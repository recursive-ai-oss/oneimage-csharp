using System.Diagnostics;
using OneImage.Codecs.WebP;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>Size and quality reports against libwebp. Run only when ONEIMAGE_WEBP_REPORT names an output file.</summary>
public sealed class WebPReports
{
    private static string? ReportPath => Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_REPORT");

    [Fact]
    public void LosslessSizeVersusLibwebp()
    {
        Assert.SkipUnless(ReportPath != null, "report only");
        var lines = new List<string>();
        foreach ((string name, byte[] rgba, int w, int h) in WebPCorpus.Photos(large: Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_LARGE") != null))
        {
            byte[] theirs = WebPOracle.SkiaEncode(rgba, w, h, lossless: true, quality: 75);
            byte[] theirs100 = WebPOracle.SkiaEncode(rgba, w, h, lossless: true, quality: 100);
            foreach (int effort in new[] { 2, 4, 6 })
            {
                var sw = Stopwatch.StartNew();
                byte[] ours = VP8LEncoder.Encode(WebPLosslessEncodeTests.ToArgb(rgba), w, h, false, new VP8LEncodeOptions { Effort = effort }, CancellationToken.None);
                sw.Stop();
                lines.Add($"{name,-14} {w}x{h} effort {effort}: ours {ours.Length,9} libwebp q75 {theirs.Length,9} q100 {theirs100.Length,9} ratio {(double)ours.Length / theirs100.Length:F3} / q75 {(double)ours.Length / theirs.Length:F3} time {sw.ElapsedMilliseconds} ms");
            }
        }

        File.AppendAllLines(ReportPath!, lines);
    }

    [Fact]
    public void LossySizeAndQualityVersusLibwebp()
    {
        Assert.SkipUnless(ReportPath != null, "report only");
        var lines = new List<string>();
        foreach ((string name, byte[] rgba, int w, int h) in WebPCorpus.Photos(large: Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_LARGE") != null))
        {
            foreach (int q in new[] { 40, 75, 90 })
            {
                var sw = Stopwatch.StartNew();
                byte[] ours = VP8Encoder.Encode(rgba, w, h, q, 4, CancellationToken.None);
                sw.Stop();
                byte[] file = WebPLosslessEncodeTests.WrapChunk("VP8 "u8, ours);
                byte[] theirs = WebPOracle.SkiaEncode(rgba, w, h, lossless: false, quality: q);
                byte[] ourDec = WebPOracle.SkiaDecode(file, out _, out _);
                byte[] theirDec = WebPOracle.SkiaDecode(theirs, out _, out _);
                lines.Add($"{name,-14} q{q}: ours {file.Length,8} B psnr {Quality.Psnr(rgba, ourDec):F2} ssim {Quality.Ssim(rgba, ourDec, w, h):F4} | libwebp {theirs.Length,8} B psnr {Quality.Psnr(rgba, theirDec):F2} ssim {Quality.Ssim(rgba, theirDec, w, h):F4} | size ratio {(double)file.Length / theirs.Length:F3} time {sw.ElapsedMilliseconds} ms");
            }
        }

        File.AppendAllLines(ReportPath!, lines);
    }
}
