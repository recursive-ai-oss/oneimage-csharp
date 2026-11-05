using System.Globalization;
using System.Reflection;
using OneImage.Metafile;

namespace OneImage.Metafile.Tests;

/// <summary>
/// Compares the raster sink with what GDI+ drew for EMF+ files (tests/testdata/metafile/oracle-gdiplus): each drawing recorded as EMF+ only
/// and as EMF+ dual. Both variants must give the same picture as GDI+ does for them. Anti-aliasing is on, as GDI+ drew with the smoothing
/// mode of each case.
/// </summary>
public class WindowsGdiPlusReferenceTests
{
    private static string Dir()
    {
        string root = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "tests", "testdata", "metafile", "oracle-gdiplus"));
    }

    public static IEnumerable<string> Cases() => Directory.GetFiles(Dir(), "*.emf").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => File.Exists(Path.Combine(Dir(), n + "_gdiplus_256x256.png"))).Order();

    public static TheoryData<string> CaseData() => [.. Cases()];

    internal static (int Diff, double Fraction) Compare(string name, int width, int height, int tolerance = 48)
    {
        byte[] emf = File.ReadAllBytes(Path.Combine(Dir(), name + ".emf"));
        var config = new ImageConfiguration().Add(new MetafileFormat(fonts: MetafileRenderTests.TestFonts(), antialias: true, background: new Rgba32(255, 255, 255, 255)));
        Rgba32[] ours = Image.Load(emf, new LoadOptions { Configuration = config }).Resize(width: width, height: height, fit: Fit.Stretch).ToArray<Rgba32>();
        Rgba32[] theirs = Image.Load(Path.Combine(Dir(), $"{name}_gdiplus_{width}x{height}.png")).ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
        string? dump = Environment.GetEnvironmentVariable("ONEIMAGE_ORACLE_DUMP");
        var off = new bool[ours.Length];
        int diff = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            Rgba32 a = ours[i], b = theirs[i];
            off[i] = Math.Abs(a.R - b.R) > tolerance || Math.Abs(a.G - b.G) > tolerance || Math.Abs(a.B - b.B) > tolerance;
            if (off[i])
            {
                diff++;
            }
        }

        if (dump is not null)
        {
            // Ours | GDI+ | difference, side by side.
            var sheet = new Rgba32[width * 3 * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    sheet[(y * width * 3) + x] = ours[i];
                    sheet[(y * width * 3) + width + x] = theirs[i];
                    sheet[(y * width * 3) + (2 * width) + x] = off[i] ? new Rgba32(255, 0, 0, 255) : new Rgba32(255, 255, 255, 255);
                }
            }

            using var c = new Canvas(width * 3, height, PixelFormat.Rgba8);
            c.CopyFrom<Rgba32>(sheet, new ImageRect(0, 0, width * 3, height));
            c.Snapshot().Save(Path.Combine(dump, $"{name}_{width}x{height}.png"));
        }

        return (diff, diff / (double)ours.Length);
    }

    [Fact]
    public void ReportAgreement()
    {
        var lines = new List<string>();
        foreach (string name in Cases())
        {
            (int d1, double f1) = Compare(name, 256, 256);
            (int d2, double f2) = Compare(name, 160, 120);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name,-26} 256x256: {100 * (1 - f1),6:F2}% ({d1} px off)   160x120: {100 * (1 - f2),6:F2}% ({d2} px off)"));
        }

        string? outFile = Environment.GetEnvironmentVariable("ONEIMAGE_ORACLE_REPORT");
        if (outFile is not null)
        {
            File.WriteAllLines(outFile, lines);
        }

        Assert.NotEmpty(lines);
    }

    /// <summary>A floor, not a target: every case agrees with GDI+ on at least 90% of its pixels at both sizes (see docs/metafile.md for the known differences).</summary>
    [Theory]
    [MemberData(nameof(CaseData))]
    public void EveryCaseAgreesWithGdiPlusOnMostPixels(string name)
    {
        Assert.True(Compare(name, 256, 256).Fraction < 0.10, name + " at 256x256");
        Assert.True(Compare(name, 160, 120).Fraction < 0.10, name + " at 160x120");
    }
}
