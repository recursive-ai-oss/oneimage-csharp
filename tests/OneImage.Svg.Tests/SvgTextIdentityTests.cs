using System.Reflection;
using System.Security.Cryptography;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

/// <summary>
/// Hashes of the rendered text tests of the resvg collection (the committed subset with the Noto subset fonts, and the full collection
/// when <c>ONEIMAGE_RESVG_TESTS</c> is set), captured from released code, so a change underneath the text package that must not alter
/// output can prove it. Set <c>ONEIMAGE_CAPTURE_SVG_TEXT</c> to a path to write a new reference instead of comparing.
/// </summary>
[Collection(SvgSerial.Name)]
public class SvgTextIdentityTests
{
    private static string Repo => typeof(SvgTextIdentityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;

    private static ImageConfiguration SubsetConfig()
    {
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        fonts.AddFont(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSans-Subset-Regular.ttf"));
        fonts.AddFont(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSans-Subset-Bold-kern.ttf"));
        fonts.AddFont(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSansCJK-Subset.otf"));
        fonts.SetGenericFamily("sans-serif", "Noto Sans");
        fonts.SetGenericFamily("serif", "Noto Sans");
        fonts.SetGenericFamily("monospace", "Noto Sans");
        fonts.FallbackFamilies.Add("Noto Sans CJK JP");
        return new ImageConfiguration().Add(new SvgFormat(fonts: fonts));
    }

    private static SortedDictionary<string, string> Compute(string root, ImageConfiguration config)
    {
        string[] files = Directory.GetFiles(Path.Combine(root, "tests", "text"), "*.svg", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var results = new string[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try
            {
                Image svg = Image.Load(files[i], new LoadOptions { Configuration = config });
                Rgba32[] px = svg.ToArray<Rgba32>();
                results[i] = svg.Width + "x" + svg.Height + " " + Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(px)));
            }
            catch (ImageException ex)
            {
                results[i] = "error " + ex.GetType().Name;
            }
        });

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < files.Length; i++)
        {
            map[Path.GetRelativePath(root, files[i]).Replace('\\', '/')] = results[i];
        }

        return map;
    }

    private static void Check(string reference, SortedDictionary<string, string> actual)
    {
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_SVG_TEXT");
        if (capture != null)
        {
            File.WriteAllLines(capture + "." + reference, actual.Select(p => p.Key + "\t" + p.Value));
            return;
        }

        var expected = File.ReadAllLines(Path.Combine(Repo, "tests", "testdata", "svg-text", reference + ".txt")).Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        var diffs = actual.Where(p => !expected.TryGetValue(p.Key, out string? e) || e != p.Value).Select(p => p.Key).ToList();
        diffs.AddRange(expected.Keys.Where(k => !actual.ContainsKey(k)));
        Assert.True(diffs.Count == 0, $"{diffs.Count} of {expected.Count} differ:\n" + string.Join('\n', diffs.Take(40)));
    }

    [Fact]
    public void CommittedTextTestsRenderIdentically() =>
        Check("subset", Compute(Path.Combine(Repo, "tests", "testdata", "resvg"), SubsetConfig()));

    [Fact]
    public void FullCollectionTextTestsRenderIdentically()
    {
        string? root = Environment.GetEnvironmentVariable("ONEIMAGE_RESVG_TESTS");
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        // The variable points at the folder that holds tests/ and fonts/ (the printed path of the fetch script).
        Check("full", Compute(root, ResvgConformance.ConfigFor(root)));
    }
}
