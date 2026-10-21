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

    private static byte[] ExtractVp8(byte[] file)
    {
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            if (file[pos] == (byte)'V' && file[pos + 1] == (byte)'P' && file[pos + 2] == (byte)'8' && file[pos + 3] == (byte)' ')
            {
                return file.AsSpan(pos + 8, (int)size).ToArray();
            }

            pos += 8 + (int)size + (int)(size & 1);
        }

        throw new InvalidOperationException();
    }

    [Fact]
    public void LossySizeAndQualityVersusLibwebp()
    {
        Assert.SkipUnless(ReportPath != null, "report only");
        var lines = new List<string>();
        string? lam = Environment.GetEnvironmentVariable("ONEIMAGE_LAMBDA");
        if (lam != null)
        {
            VP8Encoder.LambdaScale = double.Parse(lam, System.Globalization.CultureInfo.InvariantCulture);
        }

        int effort = int.Parse(Environment.GetEnvironmentVariable("ONEIMAGE_EFFORT") ?? "4", System.Globalization.CultureInfo.InvariantCulture);
        foreach ((string name, byte[] rgba, int w, int h) in WebPCorpus.Photos(large: Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_LARGE") != null))
        {
            foreach (int q in new[] { 40, 75, 90 })
            {
                var sw = Stopwatch.StartNew();
                byte[] ours = VP8Encoder.Encode(rgba, w, h, q, effort, CancellationToken.None);
                sw.Stop();
                byte[] file = WebPLosslessEncodeTests.WrapChunk("VP8 "u8, ours);
                byte[] theirs = WebPOracle.SkiaEncode(rgba, w, h, lossless: false, quality: q);
                byte[] ourDec = WebPOracle.SkiaDecode(file, out _, out _);
                byte[] theirDec = WebPOracle.SkiaDecode(theirs, out _, out _);
                lines.Add("   ours: " + Vp8HeaderDump.Describe(ours));
                lines.Add("   libwebp: " + Vp8HeaderDump.Describe(ExtractVp8(theirs)));
                lines.Add($"{name,-14} q{q}: ours {file.Length,8} B psnr {Quality.Psnr(rgba, ourDec):F2} ssim {Quality.Ssim(rgba, ourDec, w, h):F4} | libwebp {theirs.Length,8} B psnr {Quality.Psnr(rgba, theirDec):F2} ssim {Quality.Ssim(rgba, theirDec, w, h):F4} | size ratio {(double)file.Length / theirs.Length:F3} time {sw.ElapsedMilliseconds} ms");
            }
        }

        File.AppendAllLines(ReportPath!, lines);
    }
}

internal static class Vp8HeaderDump
{
    /// <summary>Parses the frame header of a VP8 payload with the decoder and returns its main fields.</summary>
    public static string Describe(byte[] vp8)
    {
        var ctor = typeof(VP8Decoder).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        object d = ctor.Invoke([vp8, vp8.Length, ImageLimits.Default, CancellationToken.None]);
        typeof(VP8Decoder).GetMethod("ParseHeaders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(d, null);
        T Get<T>(string n) => (T)typeof(VP8Decoder).GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(d)!;
        var q = Get<Array>("_quant");
        var sq = Get<int[]>("_segQuant");
        var sf = Get<int[]>("_segFilter");
        return $"useSeg={Get<bool>("_useSegment")} segQ=[{string.Join(',', sq)}] segF=[{string.Join(',', sf)}] filterLevel={Get<int>("_filterLevel")} sharp={Get<int>("_sharpness")} simple={Get<bool>("_simpleFilter")} skip={Get<bool>("_useSkipProba")}/{Get<int>("_skipProba")} uvDc={q.GetValue(0)!.GetType().GetField("UvDc")?.GetValue(q.GetValue(0))}";
    }
}
