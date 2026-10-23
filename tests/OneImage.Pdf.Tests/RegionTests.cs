namespace OneImage.Pdf.Tests;

/// <summary>A region of a PDF render is the same pixels as that part of the whole render, at every degree of parallelism.</summary>
public class RegionTests
{
    /// <summary>One page that uses every kind of paint the renderer has: a shading under a clip, a translucent group, a dashed stroke, text, a soft mask, an inline image and a tiling pattern.</summary>
    internal static byte[] BusyPage()
    {
        const string content =
            "q 20 20 360 120 re W n /Sh1 sh Q\n" +
            "q /G1 gs /Fm1 Do Q\n" +
            "q 4 w [9 4] 0 d 0 0 1 RG 10 10 m 390 290 l S Q\n" +
            "BT /F1 28 Tf 30 250 Td (Hello, region) Tj ET\n" +
            "q /GS2 gs 1 0 0 rg 0 150 400 60 re f Q\n" +
            "q 120 0 0 60 250 220 cm BI /W 2 /H 2 /CS /RGB /BPC 8 /F /AHx ID ff000000ff000000ffffff00> EI Q\n" +
            "q /Pattern cs /P1 scn 200 20 180 100 re f Q\n" +
            "q 0.5 g 0.8 w 300 300 m 340 360 380 240 390 330 c S Q\n";
        const string resources =
            "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> " +
            "/ExtGState << /G1 << /ca 0.6 /CA 0.6 >> /GS2 << /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >> >> " +
            "/Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 380 0] /Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.2 0] /C1 [0 0.3 1] /N 1 >> >> >> " +
            "/XObject << /Fm1 5 0 R >> /Pattern << /P1 7 0 R >>";
        string[] extra =
        {
            Obj("/Type /XObject /Subtype /Form /BBox [0 0 400 400] /Group << /S /Transparency /I true >> /Resources << >>", "0 0.5 0.5 rg 100 100 150 150 re f 0.5 0 0.5 rg 170 170 150 150 re f"),
            Obj("/Type /XObject /Subtype /Form /BBox [0 0 400 400] /Group << /S /Transparency /CS /DeviceGray >> /Resources << >>", "0.5 g 0 0 400 400 re f 1 g 50 150 200 60 re f"),
            Obj("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >>", "1 0 0 rg 0 0 10 10 re f 0 0.6 0 rg 10 10 10 10 re f"),
        };
        return TestPdf.Page(content, resources, width: 400, height: 400, extra: extra);
    }

    /// <summary>A stream object with the given dictionary entries and a correct /Length.</summary>
    internal static string Obj(string dict, string content) => $"<< {dict} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static Image Load(byte[] pdf, double dpi = 100) => RenderTests.Load(pdf, new PdfRenderOptions { Dpi = dpi });

    [Fact]
    public void RegionsEqualTheWholeRender()
    {
        Image img = Load(BusyPage());
        int w = img.Width, h = img.Height;
        Rgba32[] whole = img.ToArray<Rgba32>();
        Assert.Contains(whole, p => p.R != 255 || p.G != 255 || p.B != 255);
        var rects = new[]
        {
            new ImageRect(0, 0, 1, 1), new ImageRect(127, 127, 2, 2), new ImageRect(100, 40, 130, 77), new ImageRect(w - 17, h - 33, 17, 33),
            new ImageRect(1, 0, w - 1, 5), new ImageRect(0, 255, 256, 1), new ImageRect(31, 63, 257, 129), new ImageRect(0, 0, w, h),
        };
        foreach (ImageRect r in rects)
        {
            // A fresh image per region: nothing may come from tiles the whole render left behind.
            Rgba32[] part = new Rgba32[r.Width * r.Height];
            Load(BusyPage()).CopyPixelsTo<Rgba32>(part, r);
            for (int y = 0; y < r.Height; y++)
            {
                Assert.True(
                    part.AsSpan(y * r.Width, r.Width).SequenceEqual(whole.AsSpan(((r.Y + y) * w) + r.X, r.Width)),
                    $"row {y} of region {r.X},{r.Y} {r.Width}x{r.Height} differs from the whole render");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void RenderIsIdenticalAtEveryDegree(int degree)
    {
        byte[] pdf = BusyPage();
        Rgba32[] reference = Load(pdf).ToArray<Rgba32>(maxDegree: 1);
        Image img = Load(pdf);
        Rgba32[] pixels = new Rgba32[img.Width * img.Height];
        img.CopyPixelsTo<Rgba32>(pixels, new ImageRect(0, 0, img.Width, img.Height), degree);
        Assert.True(reference.AsSpan().SequenceEqual(pixels), $"degree {degree} differs from degree 1");
    }

    [Fact]
    public void ConcurrentRegionReadsOfOneImageAgree()
    {
        byte[] pdf = BusyPage();
        Image img = Load(pdf);
        Rgba32[] whole = Load(pdf).ToArray<Rgba32>();
        int w = img.Width, h = img.Height;
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var r = new ImageRect((i * 37) % (w - 60), (i * 53) % (h - 60), 60, 60);
            var part = new Rgba32[r.Width * r.Height];
            img.CopyPixelsTo<Rgba32>(part, r);
            for (int y = 0; y < r.Height; y++)
            {
                Assert.True(part.AsSpan(y * r.Width, r.Width).SequenceEqual(whole.AsSpan(((r.Y + y) * w) + r.X, r.Width)));
            }
        });
    }

    [Fact]
    public void ResizedRenderUsesTheTargetSizeDirectly()
    {
        // Thumbnail-style use: the page is drawn at the final size, not drawn large and reduced.
        Image img = Load(BusyPage(), 150).Resize(width: 150);
        Assert.Equal(150, img.Width);
        Assert.Equal(150, img.Height);
        Assert.Contains(img.ToArray<Rgba32>(), p => p.R != 255);
    }
}
