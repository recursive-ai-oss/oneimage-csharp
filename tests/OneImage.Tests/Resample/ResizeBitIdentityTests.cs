using Xunit;

namespace OneImage.Tests.Resample;

/// <summary>
/// Resize output must stay byte for byte what 1.6.0 produced, on every platform. The only references that were re-captured are
/// 16-bit and float linear-light cases (47 of 2371): their last bits changed when the sRGB curve stopped using the platform math
/// library. The reference file holds one hash per case of
/// <see cref="ResizeMatrix"/>; set <c>ONEIMAGE_CAPTURE_RESIZE</c> to a path to write a new reference file instead of comparing.
/// </summary>
public class ResizeBitIdentityTests
{
    private static readonly Lazy<Dictionary<string, string>> s_reference = new(LoadReference);

    private static Dictionary<string, string> LoadReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "resize", "reference.txt");
        var map = new Dictionary<string, string>();
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0)
            {
                map[line[..tab]] = line[(tab + 1)..];
            }
        }

        return map;
    }

    [Fact]
    public void ImageResizeMatchesReference()
    {
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_RESIZE");
        var lines = new List<string>();
        var failures = new List<string>();
        int checkedCases = 0;
        foreach (IGrouping<(string, int, int), ResizeMatrix.Case> group in ResizeMatrix.All().GroupBy(c => (c.Format, c.SourceWidth, c.SourceHeight)))
        {
            (Image image, _, _) = ResizeMatrix.Source(group.Key.Item1, group.Key.Item2, group.Key.Item3);
            foreach (ResizeMatrix.Case c in group)
            {
                Image resized = image.Resize(c.Width, c.Height, Fit.Stretch, filter: c.Filter, linear: c.Linear);
                Assert.Equal((c.Width, c.Height), (resized.Width, resized.Height));
                string hash = ResizeMatrix.Hash(ResizeMatrix.Read(resized));
                lines.Add($"{c.Key}\t{hash}");
                checkedCases++;
                if (capture == null && (!s_reference.Value.TryGetValue(c.Key, out string? expected) || expected != hash))
                {
                    failures.Add(c.Key);
                }
            }
        }

        // The default call of the target use case: height only, Contain, Auto filter, linear light.
        (Image gray, _, _) = ResizeMatrix.Source("Gray8", 2300, 60);
        Image def = gray.Resize(height: 32);
        string defHash = ResizeMatrix.Hash(ResizeMatrix.Read(def));
        lines.Add($"default|Gray8|2300x60|height32|{def.Width}x{def.Height}\t{defHash}");
        if (capture == null && (!s_reference.Value.TryGetValue($"default|Gray8|2300x60|height32|{def.Width}x{def.Height}", out string? e) || e != defHash))
        {
            failures.Add("default height:32");
        }

        if (capture != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
            File.WriteAllLines(capture, lines);
            return;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCases} differ from the 1.6.0 reference, first: {string.Join("; ", failures.Take(8))}");
        Assert.True(checkedCases > 1000);
    }

    [Fact]
    public void SpanResizeMatchesReference()
    {
        var failures = new List<string>();
        int checkedCases = 0;
        using var resizer = new PixelResizer();
        foreach (IGrouping<(string, int, int), ResizeMatrix.Case> group in ResizeMatrix.All().GroupBy(c => (c.Format, c.SourceWidth, c.SourceHeight)))
        {
            (_, byte[] bytes, PixelFormat format) = ResizeMatrix.Source(group.Key.Item1, group.Key.Item2, group.Key.Item3);
            foreach (ResizeMatrix.Case c in group)
            {
                byte[] output = new byte[c.Width * c.Height * format.BytesPerPixel];
                resizer.ResizeRaw(bytes, format, c.SourceWidth, c.SourceHeight, 0, output, c.Width, c.Height, 0, c.Filter, c.Linear);
                checkedCases++;
                if (!s_reference.Value.TryGetValue(c.Key, out string? expected) || expected != ResizeMatrix.Hash(output))
                {
                    failures.Add(c.Key);
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCases} differ from the 1.6.0 reference, first: {string.Join("; ", failures.Take(8))}");
    }

    [Fact]
    public void TypedAndStridedSpanResizeEqualTightRaw()
    {
        using var resizer = new PixelResizer();
        foreach (string name in new[] { "Gray8", "Gray16", "Rgb24", "Rgba32" })
        {
            (_, byte[] bytes, PixelFormat format) = ResizeMatrix.Source(name, 129, 257);
            int bpp = format.BytesPerPixel;
            byte[] tight = new byte[65 * 100 * bpp];
            resizer.ResizeRaw(bytes, format, 129, 257, 0, tight, 65, 100, 0);

            // Same pixels in a padded source and into a padded destination.
            int srcStride = (129 * bpp) + (3 * bpp);
            byte[] padded = new byte[srcStride * 257];
            for (int y = 0; y < 257; y++)
            {
                bytes.AsSpan(y * 129 * bpp, 129 * bpp).CopyTo(padded.AsSpan(y * srcStride));
            }

            int dstStride = (65 * bpp) + (5 * bpp);
            byte[] outPadded = new byte[dstStride * 100];
            resizer.ResizeRaw(padded, format, 129, 257, srcStride, outPadded, 65, 100, dstStride);
            for (int y = 0; y < 100; y++)
            {
                Assert.True(tight.AsSpan(y * 65 * bpp, 65 * bpp).SequenceEqual(outPadded.AsSpan(y * dstStride, 65 * bpp)), $"{name} row {y}");
            }
        }

        (_, byte[] g, _) = ResizeMatrix.Source("Gray8", 300, 200);
        var gray = OneImage.Kernels.SpanCast.As<Gray8>(g);
        var typed = new Gray8[75 * 50];
        resizer.Resize<Gray8>(gray, 300, 200, typed, 75, 50);
        byte[] raw = new byte[75 * 50];
        resizer.ResizeRaw(g, PixelFormat.Gray8, 300, 200, 0, raw, 75, 50, 0);
        Assert.True(OneImage.Kernels.SpanCast.AsBytes<Gray8>(typed).SequenceEqual(raw));
    }
}
