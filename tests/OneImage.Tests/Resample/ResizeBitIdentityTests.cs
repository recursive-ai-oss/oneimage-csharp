using Xunit;

namespace OneImage.Tests.Resample;

/// <summary>
/// Resize output must stay byte for byte what 1.6.0 produced. The reference file holds one hash per case of
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
}
