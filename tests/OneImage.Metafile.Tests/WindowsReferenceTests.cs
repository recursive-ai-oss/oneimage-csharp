using System.Globalization;
using System.Reflection;
using OneImage.Metafile;

namespace OneImage.Metafile.Tests;

/// <summary>
/// Compares the raster sink with what Windows GDI drew for the same EMF files (tests/testdata/metafile/oracle). GDI does not
/// anti-alias, so the comparison renders without anti-aliasing and counts pixels that differ by more than a small tolerance.
/// </summary>
public class WindowsReferenceTests
{
    private static string Dir()
    {
        string root = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "tests", "testdata", "metafile", "oracle"));
    }

    public static IEnumerable<string> Cases() => Directory.GetFiles(Dir(), "*.emf").Select(Path.GetFileNameWithoutExtension).Order()!;

    public static TheoryData<string> CaseData() => [.. Cases()];

    /// <summary>Renders one case at <paramref name="size"/> square and returns (pixels that differ, total, size).</summary>
    internal static (int Diff, int Total, double Fraction) Compare(string name, int size, int tolerance = 48)
    {
        byte[] emf = File.ReadAllBytes(Path.Combine(Dir(), name + ".emf"));
        var config = new ImageConfiguration().Add(new MetafileFormat(fonts: MetafileRenderTests.TestFonts(), antialias: false, background: new Rgba32(255, 255, 255, 255)));
        Rgba32[] ours = Image.Load(emf, new LoadOptions { Configuration = config }).Resize(width: size, height: size, fit: Fit.Stretch).ToArray<Rgba32>();
        Rgba32[] theirs = Image.Load(Path.Combine(Dir(), $"{name}_gdi_{size}x{size}.png")).ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
        string? dump = Environment.GetEnvironmentVariable("ONEIMAGE_ORACLE_DUMP");
        if (dump is not null)
        {
            // Ours | Windows | difference, side by side, for looking at.
            var sheet = new Rgba32[size * 3 * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Rgba32 a = ours[(y * size) + x], b = theirs[(y * size) + x];
                    bool off = Math.Abs(a.R - b.R) > tolerance || Math.Abs(a.G - b.G) > tolerance || Math.Abs(a.B - b.B) > tolerance;
                    sheet[(y * size * 3) + x] = a;
                    sheet[(y * size * 3) + size + x] = b;
                    sheet[(y * size * 3) + (2 * size) + x] = off ? new Rgba32(255, 0, 0, 255) : new Rgba32(255, 255, 255, 255);
                }
            }

            using var c = new Canvas(size * 3, size, PixelFormat.Rgba8);
            c.CopyFrom<Rgba32>(sheet, new ImageRect(0, 0, size * 3, size));
            c.Snapshot().Save(Path.Combine(dump, $"{name}_{size}.png"));
        }

        int diff = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            Rgba32 a = ours[i], b = theirs[i];
            if (Math.Abs(a.R - b.R) > tolerance || Math.Abs(a.G - b.G) > tolerance || Math.Abs(a.B - b.B) > tolerance)
            {
                diff++;
            }
        }

        return (diff, ours.Length, diff / (double)ours.Length);
    }

    [Fact]
    public void ReportAgreement()
    {
        var lines = new List<string>();
        foreach (string name in Cases())
        {
            (int d256, _, double f256) = Compare(name, 256);
            (int d512, _, double f512) = Compare(name, 512);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name,-18} 256: {100 * (1 - f256),6:F2}% ({d256} px off)   512: {100 * (1 - f512),6:F2}% ({d512} px off)"));
        }

        string? outFile = Environment.GetEnvironmentVariable("ONEIMAGE_ORACLE_REPORT");
        if (outFile is not null)
        {
            File.WriteAllLines(outFile, lines);
        }

        Assert.NotEmpty(lines);
    }

    /// <summary>
    /// A floor, not a target: every case agrees with Windows on at least 95% of its pixels at both sizes. The differences that remain are
    /// one-pixel edge placement at fractional scales (GDI rounds shape corners to its own pixel grid), fonts that are not Arial, bitmap
    /// smoothing in stretched images, and the gaps listed in docs/metafile.md (WidenPath, text in paths).
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseData))]
    public void EveryCaseAgreesWithWindowsOnMostPixels(string name)
    {
        Assert.True(Compare(name, 256).Fraction < 0.05, name + " at 256");
        Assert.True(Compare(name, 512).Fraction < 0.05, name + " at 512");
    }
}
