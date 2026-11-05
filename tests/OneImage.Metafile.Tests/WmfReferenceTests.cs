using System.Globalization;
using System.Reflection;
using OneImage.Metafile;

namespace OneImage.Metafile.Tests;

/// <summary>
/// Compares the raster sink with what Windows GDI drew when it played the same Windows metafiles (tests/testdata/metafile/oracle). Each case exists
/// with and without the placeable header; both must give the same picture as the 256 x 256 render of <c>PlayMetaFile</c>.
/// </summary>
public class WmfReferenceTests
{
    private static string Dir()
    {
        string root = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "tests", "testdata", "metafile", "oracle"));
    }

    public static IEnumerable<string> Cases() => Directory.GetFiles(Dir(), "*_wmf_gdi_256x256.png").Select(f => Path.GetFileName(f)[..^"_wmf_gdi_256x256.png".Length]).Order();

    public static TheoryData<string, bool> CaseData()
    {
        var data = new TheoryData<string, bool>();
        foreach (string c in Cases())
        {
            data.Add(c, true);
            data.Add(c, false);
        }

        return data;
    }

    internal static double Compare(string name, bool placeable, int tolerance = 48)
    {
        const int size = 256;
        byte[] wmf = File.ReadAllBytes(Path.Combine(Dir(), name + (placeable ? ".wmf" : "_nonplaceable.wmf")));
        var config = new ImageConfiguration().Add(new WmfFormat(fonts: MetafileRenderTests.TestFonts(), antialias: false, background: new Rgba32(255, 255, 255, 255)));
        Image image = Image.Load(wmf, new LoadOptions { Configuration = config });
        Assert.Equal((size, size), (image.Width, image.Height));
        Rgba32[] ours = image.ToArray<Rgba32>();
        Rgba32[] theirs = Image.Load(Path.Combine(Dir(), $"{name}_wmf_gdi_256x256.png")).ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
        string? dump = Environment.GetEnvironmentVariable("ONEIMAGE_ORACLE_DUMP");
        var sheet = dump is null ? null : new Rgba32[size * 3 * size];
        int diff = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Rgba32 a = ours[(y * size) + x], b = theirs[(y * size) + x];
                bool off = Math.Abs(a.R - b.R) > tolerance || Math.Abs(a.G - b.G) > tolerance || Math.Abs(a.B - b.B) > tolerance;
                diff += off ? 1 : 0;
                if (sheet is not null)
                {
                    sheet[(y * size * 3) + x] = a;
                    sheet[(y * size * 3) + size + x] = b;
                    sheet[(y * size * 3) + (2 * size) + x] = off ? new Rgba32(255, 0, 0, 255) : new Rgba32(255, 255, 255, 255);
                }
            }
        }

        if (sheet is not null && placeable)
        {
            using var c = new Canvas(size * 3, size, PixelFormat.Rgba8);
            c.CopyFrom<Rgba32>(sheet, new ImageRect(0, 0, size * 3, size));
            c.Snapshot().Save(Path.Combine(dump!, $"wmf_{name}.png"));
        }

        return diff / (double)(size * size);
    }

    [Fact]
    public void ReportAgreement()
    {
        string? outFile = Environment.GetEnvironmentVariable("ONEIMAGE_ORACLE_REPORT");
        if (outFile is null)
        {
            return;
        }

        var lines = new List<string>();
        foreach (string name in Cases())
        {
            double p = Compare(name, true), n = Compare(name, false);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name,-14} placeable: {100 * (1 - p),6:F2}%   nonplaceable: {100 * (1 - n),6:F2}%"));
        }

        File.WriteAllLines(outFile + ".wmf", lines);
    }

    /// <summary>
    /// A floor, not a target: every case agrees with Windows on at least 95% of its pixels, with and without the placeable header. The one
    /// exception is <c>text_styled</c>, which is mostly text in Arial, Times New Roman and Courier New, faces the test fonts replace with
    /// Noto Sans: its floor is 90%.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseData))]
    public void EveryWmfCaseAgreesWithWindowsOnMostPixels(string name, bool placeable) =>
        Assert.True(Compare(name, placeable) < (name == "text_styled" ? 0.10 : 0.05), name);
}
