using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using OneImage.Svg;
using OneImage.Text;
using Xunit;

namespace OneImage.Svg.Tests;

/// <summary>
/// Renders the resvg test collection and scores each result against its reference PNG.
/// By default it uses the subset committed in <c>tests/testdata/resvg</c>; set <c>ONEIMAGE_RESVG_TESTS</c> to the folder
/// printed by <c>tools/fetch-resvg-tests.sh</c> to use the full collection with its fonts.
/// Set <c>ONEIMAGE_SVG_REPORT</c> to a file path to also write the table there.
/// </summary>
/// <remarks>
/// Scoring: both images are composited over white; a pixel differs when any channel is off by more than
/// <see cref="ChannelTolerance"/> (of 255); a test passes when at most <see cref="MaxDifferingFraction"/> of its pixels differ.
/// The tolerance absorbs anti-aliasing differences between rasterizers, not shape or color errors.
/// </remarks>
public sealed partial class ResvgConformance
{
    /// <summary>Largest per-channel difference (0..255) still counted as the same pixel.</summary>
    public const int ChannelTolerance = 24;

    /// <summary>Fraction of pixels allowed to differ by more than the tolerance.</summary>
    public const double MaxDifferingFraction = 0.0025;

    private static readonly Dictionary<string, ImageConfiguration> Configs = [];

    /// <summary>
    /// The configuration for a collection: the SVG format with the collection's own fonts when it has a <c>fonts</c> folder (the full collection
    /// does; the committed subset does not), with the generic families the reference images were made with and no installed fonts.
    /// </summary>
    private static ImageConfiguration ConfigFor(string root)
    {
        lock (Configs)
        {
            if (Configs.TryGetValue(root, out ImageConfiguration? cached))
            {
                return cached;
            }

            string fonts = Path.Combine(root, "fonts");
            SvgFormat format = SvgFormat.Instance;
            if (Directory.Exists(fonts))
            {
                // The reference images were made with a 12 px medium font; browsers use 16, which is our default.
                var options = new SvgFontOptions { UseSystemFonts = false, DefaultFontSize = 12 };
                foreach (string file in Directory.GetFiles(fonts, "*.*").Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                {
                    try
                    {
                        options.AddFont(file);
                    }
                    catch (FontFormatException)
                    {
                        // A font the library cannot read (a bitmap-only color font): the tests that need it fail on their own.
                    }
                }

                options.SetGenericFamily("serif", "Noto Serif");
                options.SetGenericFamily("sans-serif", "Noto Sans");
                options.SetGenericFamily("monospace", "Noto Mono");
                options.SetGenericFamily("cursive", "Yellowtail");
                options.SetGenericFamily("fantasy", "Sedgwick Ave Display");
                options.FallbackFamilies.Add("Noto Sans");
                format = new SvgFormat(fonts: options);
            }

            return Configs[root] = new ImageConfiguration().Add(format);
        }
    }

    /// <summary>Outcome of one test file.</summary>
    internal sealed record Result(string Category, string Name, string Outcome, string Detail);

    [GeneratedRegex(@"(xlink:)?href\s*=\s*[""'](?!#|data:)[^""']", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalHref();

    [GeneratedRegex(@"<(script|animate|animateTransform|animateMotion|set|foreignObject)\b|@import", RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedFeature();

    internal static string RootFolder()
    {
        string? env = Environment.GetEnvironmentVariable("ONEIMAGE_RESVG_TESTS");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        string repo = typeof(ResvgConformance).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        return Path.Combine(repo, "tests", "testdata", "resvg");
    }

    /// <summary>Returns why a test is outside the scope of this library, or <see langword="null"/> when it should render.</summary>
    internal static string? Exclusion(string svgText)
    {
        if (svgText.Contains("<!ENTITY", StringComparison.Ordinal) || svgText.Contains("<!DOCTYPE", StringComparison.Ordinal))
        {
            return "DTD or entity declaration (refused by policy)";
        }

        if (UnsupportedFeature().IsMatch(svgText))
        {
            return "script, animation, foreignObject or @import (not supported)";
        }

        if (ExternalHref().IsMatch(svgText))
        {
            return "external file reference (refused by policy)";
        }

        return null;
    }

    /// <summary>Compares two straight-alpha RGBA buffers of equal size; returns the fraction of differing pixels.</summary>
    internal static double DifferingFraction(ReadOnlySpan<Rgba32> actual, ReadOnlySpan<Rgba32> expected)
    {
        int bad = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            (int ar, int ag, int ab) = OverWhite(actual[i]);
            (int er, int eg, int eb) = OverWhite(expected[i]);
            if (Math.Abs(ar - er) > ChannelTolerance || Math.Abs(ag - eg) > ChannelTolerance || Math.Abs(ab - eb) > ChannelTolerance)
            {
                bad++;
            }
        }

        return expected.Length == 0 ? 0 : (double)bad / expected.Length;
    }

    private static (int, int, int) OverWhite(Rgba32 p) =>
        (Blend(p.R, p.A), Blend(p.G, p.A), Blend(p.B, p.A));

    private static int Blend(int c, int a) => (c * a + 255 * (255 - a) + 127) / 255;

    private static Result RunOne(string root, string svgPath)
    {
        string rel = Path.GetRelativePath(Path.Combine(root, "tests"), svgPath).Replace('\\', '/');
        string category = rel[..rel.IndexOf('/', StringComparison.Ordinal)];
        string name = rel[..^4];
        string? exclusion = Exclusion(File.ReadAllText(svgPath));
        if (exclusion is not null)
        {
            return new Result(category, name, "excluded", exclusion);
        }

        string pngPath = Path.ChangeExtension(svgPath, ".png");
        if (!File.Exists(pngPath))
        {
            return new Result(category, name, "excluded", "no reference image");
        }

        try
        {
            Image reference = Image.Load(pngPath);
            int w = reference.Width;
            int h = reference.Height;
            Rgba32[] expected = reference.ToArray<Rgba32>();

            // Render at the reference size, the way a thumbnail request would.
            Image svg = Image.Load(svgPath, new LoadOptions { Configuration = ConfigFor(root) });
            Image sized = svg.Resize(w, h, Fit.Stretch);
            Rgba32[] actual = sized.ToArray<Rgba32>();
            double bad = DifferingFraction(actual, expected);
            if ((bad > MaxDifferingFraction || Environment.GetEnvironmentVariable("ONEIMAGE_SVG_DUMP_ALL") is { Length: > 0 }) && Environment.GetEnvironmentVariable("ONEIMAGE_SVG_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                sized.Save(Path.Combine(dump, name.Replace('/', '_') + ".png"));
            }

            return bad <= MaxDifferingFraction
                ? new Result(category, name, "pass", string.Empty)
                : new Result(category, name, "fail", $"{bad:P2} of pixels differ");
        }
        catch (ImageException ex)
        {
            return new Result(category, name, "error", ex.Message);
        }
    }

    internal static IReadOnlyList<Result> RunAll(string root)
    {
        string[] files = Directory.GetFiles(Path.Combine(root, "tests"), "*.svg", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable("ONEIMAGE_RESVG_ONLY") is { Length: > 0 } rx)
        {
            files = [.. files.Where(f => Regex.IsMatch(f, rx, RegexOptions.CultureInvariant))];
        }

        if (Environment.GetEnvironmentVariable("ONEIMAGE_SVG_ONLY") is { Length: > 0 } only)
        {
            // A path prefix below tests/, for example "filters/feOffset", to iterate on one area.
            files = [.. files.Where(f => Path.GetRelativePath(Path.Combine(root, "tests"), f).Replace('\\', '/').StartsWith(only, StringComparison.Ordinal))];
        }

        var results = new Result[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => results[i] = RunOne(root, files[i]));
        return results;
    }

    internal static string Table(IReadOnlyList<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| category | files | excluded | rendered | pass | pass rate |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        int tf = 0, te = 0, tp = 0;
        foreach (IGrouping<string, Result> g in results.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int files = g.Count();
            int excluded = g.Count(r => r.Outcome == "excluded");
            int pass = g.Count(r => r.Outcome == "pass");
            int run = files - excluded;
            tf += files;
            te += excluded;
            tp += pass;
            sb.AppendLine($"| {g.Key} | {files} | {excluded} | {run} | {pass} | {(run == 0 ? 0 : 100.0 * pass / run):F1}% |");
        }

        int trun = tf - te;
        sb.AppendLine($"| **all** | {tf} | {te} | {trun} | {tp} | {(trun == 0 ? 0 : 100.0 * tp / trun):F1}% |");
        sb.AppendLine();
        sb.AppendLine($"Per-pixel tolerance {ChannelTolerance}/255 per channel over white; a test passes with at most {MaxDifferingFraction:P2} differing pixels.");
        sb.AppendLine();
        sb.AppendLine("Exclusions:");
        foreach (IGrouping<string, Result> g in results.Where(r => r.Outcome == "excluded").GroupBy(r => r.Detail).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"- {g.Key}: {g.Count()}");
        }

        sb.AppendLine();
        sb.AppendLine("Failures by reason:");
        foreach (IGrouping<string, Result> g in results.Where(r => r.Outcome is "error" or "fail").GroupBy(r => r.Outcome == "fail" ? "differs from the reference" : r.Detail).OrderByDescending(g => g.Count()).Take(10))
        {
            sb.AppendLine($"- {g.Key}: {g.Count()}");
        }

        return sb.ToString();
    }

    [Fact]
    public void ScoringToleratesAntialiasingButNotWrongColors()
    {
        Rgba32[] a = new Rgba32[100];
        Array.Fill(a, new Rgba32(10, 20, 30, 255));
        Rgba32[] b = [.. a];
        Assert.Equal(0, DifferingFraction(a, b));
        b[0] = new Rgba32(10 + ChannelTolerance, 20, 30, 255);
        Assert.Equal(0, DifferingFraction(a, b));
        b[0] = new Rgba32(10 + ChannelTolerance + 1, 20, 30, 255);
        Assert.Equal(0.01, DifferingFraction(a, b), 6);
        b[0] = new Rgba32(200, 200, 200, 0); // fully transparent is white, not black
        Assert.Equal(0.01, DifferingFraction(a, b), 6);
    }

    [Theory]
    [InlineData("<svg><circle/></svg>", null)]
    [InlineData("<!DOCTYPE svg [<!ENTITY a 'b'>]><svg/>", "DTD or entity declaration (refused by policy)")]
    [InlineData("<svg><animate attributeName='x'/></svg>", "script, animation, foreignObject or @import (not supported)")]
    [InlineData("<svg><image xlink:href='../a.png'/></svg>", "external file reference (refused by policy)")]
    [InlineData("<svg><image href='data:image/png;base64,AAAA'/><use href='#a'/></svg>", null)]
    public void ExclusionRules(string svg, string? expected)
    {
        Assert.Equal(expected, Exclusion(svg));
    }

    [Fact]
    public void ReportsThePassRatePerCategory()
    {
        string root = RootFolder();
        Assert.True(Directory.Exists(Path.Combine(root, "tests")), $"test files not found in {root}");
        IReadOnlyList<Result> results = RunAll(root);
        Assert.True(results.Count > 1000 || Environment.GetEnvironmentVariable("ONEIMAGE_SVG_ONLY") is { Length: > 0 } || Environment.GetEnvironmentVariable("ONEIMAGE_RESVG_ONLY") is { Length: > 0 }, $"only {results.Count} test files found");

        string table = Table(results);
        Console.WriteLine(table);
        string? report = Environment.GetEnvironmentVariable("ONEIMAGE_SVG_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            File.WriteAllText(report, table);
            File.WriteAllLines(Path.ChangeExtension(report, ".csv"), results.Select(r => $"{r.Category},{r.Name},{r.Outcome},\"{r.Detail.Replace("\"", "'", StringComparison.Ordinal)}\""));
        }

        // Rendering is built in stages; the table is the product of this test, not a pass-rate gate.
        Assert.All(results, r => Assert.NotEmpty(r.Category));
    }
}
