using System.Reflection;
using OneImage.Metafile;

namespace OneImage.Metafile.Tests;

/// <summary>
/// The small EMF files under tests/testdata/metafile are written by this class (set ONEIMAGE_WRITE_TESTDATA=1 to regenerate them)
/// and the tests check that the committed bytes still equal the builder's output and that every file loads and renders.
/// </summary>
public class TestDataTests
{
    private static readonly Dictionary<string, Func<byte[]>> Files = new()
    {
        ["shapes.emf"] = Shapes,
        ["hatch-and-dash.emf"] = () => new EmfBuilder()
            .CreateBrush(1, 2, EmfBuilder.Rgb(0, 0, 0), 5).Select(1).SetBkColor(EmfBuilder.Rgb(255, 255, 0)).Rectangle(5, 5, 50, 50)
            .Ints(38, 2, 1, 1, 0, EmfBuilder.Rgb(0, 128, 0)).Select(2).MoveTo(5, 70).LineTo(95, 70).Build(),
        ["mapping-and-text.emf"] = () => new EmfBuilder()
            .MapMode(8).WindowExt(10, 10).ViewportExt(100, 100).CreateFont(1, -2, "Arial", 700).Select(1)
            .SetTextColor(EmfBuilder.Rgb(0, 128, 0)).SetBkMode(1).ExtTextOutW("Hello", 1, 2).Build(),
        ["clip-and-path.emf"] = () => new EmfBuilder()
            .CreateBrush(1, 0, EmfBuilder.Rgb(200, 0, 100)).Select(1).BeginPath().MoveTo(50, 5).LineTo(95, 95).LineTo(5, 95).CloseFigure().EndPath().SelectClipPath(5)
            .Rectangle(0, 0, 100, 50).Build(),
        ["damaged-truncated.emf"] = () =>
        {
            byte[] ok = Shapes();
            return ok[..(ok.Length - 30)];
        },
    };

    private static byte[] Shapes() => new EmfBuilder()
        .CreatePen(1, 0, 3, EmfBuilder.Rgb(0, 0, 255)).CreateBrush(2, 0, EmfBuilder.Rgb(255, 0, 0)).Select(1).Select(2)
        .Rectangle(10, 10, 60, 50).Ellipse(40, 40, 90, 90).RoundRect(5, 60, 45, 95, 10, 10).Build();

    private static string Dir()
    {
        string root = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "tests", "testdata", "metafile"));
    }

    [Fact]
    public void CommittedFilesAreWhatTheGeneratorWrites()
    {
        bool write = Environment.GetEnvironmentVariable("ONEIMAGE_WRITE_TESTDATA") == "1";
        Directory.CreateDirectory(Dir());
        foreach ((string name, Func<byte[]> make) in Files)
        {
            string path = Path.Combine(Dir(), name);
            if (write)
            {
                File.WriteAllBytes(path, make());
            }

            Assert.Equal(make(), File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void EveryCommittedFileLoadsOrFailsWithATypedError()
    {
        foreach (string path in Directory.GetFiles(Dir(), "*.emf"))
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                var (px, w, h) = MetafileRenderTests.Render(bytes, fonts: MetafileRenderTests.TestFonts());
                Assert.True(w > 0 && h > 0 && px.Length == w * h, path);
            }
            catch (ImageFormatException)
            {
                Assert.Contains("damaged", path);
            }
            catch (ImageLimitException)
            {
                Assert.Contains("fuzz-", path); // hostile coordinates beyond the drawing engine's range
            }
        }
    }

    [Fact]
    public void FuzzFindingsParseAndDrawWithoutAnUntypedException()
    {
        // Each fuzz-* file made the renderer throw an argument exception once (an infinite pen width, a blit or brush whose
        // transform could not be inverted, a path with a NaN point); they must now draw or fail with a typed error.
        string[] files = [.. Directory.GetFiles(Dir(), "fuzz-*.emf"), .. Directory.GetFiles(Dir(), "fuzz-*.wmf")];
        Assert.True(files.Length >= 4);
        var config = new ImageConfiguration().Add(new MetafileFormat(fonts: MetafileRenderTests.TestFonts())).Add(new WmfFormat(fonts: MetafileRenderTests.TestFonts()));
        foreach (string path in files)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                Image image = Image.Load(bytes, new LoadOptions { Configuration = config }).Resize(width: 48);
                Assert.NotEmpty(image.ToArray<Rgba32>());
            }
            catch (ImageException)
            {
            }
        }
    }
}
