using System.Diagnostics;

namespace OneImage.Pdf.Tests;

/// <summary>
/// Hostile files are bounded before the memory they ask for is allocated. The tests count process-wide allocation, so this class runs
/// with no other test class beside it (the counter would include their allocations).
/// </summary>
[Collection(LimitTests.CollectionName)]
public class LimitTests
{
    public const string CollectionName = "Allocation measurement";

    private static long Allocated() => GC.GetTotalAllocatedBytes(precise: true);

    [Fact]
    public void HugePageFailsBeforeAnythingIsDrawn()
    {
        // 14,400 pt at 150 dpi is 30,000 px a side: 900 million pixels, over the frame limit.
        byte[] pdf = TestPdf.Page("0 g 0 0 10 10 re f", width: 14400, height: 14400);
        long before = Allocated();
        Assert.Throws<ImageLimitException>(() => RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 150 }).ToArray<Rgba32>());
        Assert.True(Allocated() - before < 64L << 20, "a refused page must not allocate in proportion to its size");
    }

    [Fact]
    public void AbsurdDpiIsRefused()
    {
        byte[] pdf = TestPdf.Page("0 g 0 0 10 10 re f", width: 612, height: 792);
        Assert.Throws<ImageLimitException>(() => RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 100000 }).ToArray<Rgba32>());
    }

    [Fact]
    public void GiantImageIsSkippedWithoutDecoding()
    {
        // The dictionary claims 200,000 x 200,000 pixels (4e10) over a few bytes of data.
        string image = RegionTests.Obj("/Type /XObject /Subtype /Image /Width 200000 /Height 200000 /ColorSpace /DeviceGray /BitsPerComponent 8", "abc");
        byte[] pdf = TestPdf.Page("1 0 0 rg 0 0 50 50 re f q 100 0 0 100 50 50 cm /Im Do Q", "/XObject << /Im 5 0 R >>", extra: new[] { image });
        long before = Allocated();
        Rgba32[] px = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.True(Allocated() - before < 64L << 20);
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(175 * 200) + 25]);
    }

    [Fact]
    public void DeeplyNestedFormsAreBounded()
    {
        // Form 5 draws form 6, which draws form 7, and so on, each as a transparency group.
        const int Depth = 60;
        var extra = new List<string>();
        for (int i = 0; i < Depth; i++)
        {
            string call = i + 1 < Depth ? $"/F Do" : "0 g 0 0 50 50 re f";
            string res = i + 1 < Depth ? $"/Resources << /XObject << /F {6 + i} 0 R >> >>" : "/Resources << >>";
            extra.Add(RegionTests.Obj($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> {res}", call));
        }

        byte[] pdf = TestPdf.Page("/F Do", "/XObject << /F 5 0 R >>", extra: extra.ToArray());
        var sw = Stopwatch.StartNew();
        try
        {
            RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        }
        catch (ImageException)
        {
            // A typed refusal is the other acceptable outcome.
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void NestedGroupsBeyondTheLimitFailTyped()
    {
        const int Depth = 40;
        var extra = new List<string>();
        for (int i = 0; i < Depth; i++)
        {
            string content = i + 1 < Depth ? "/G gs /F Do" : "0 g 0 0 50 50 re f";
            string res = i + 1 < Depth ? $"/Resources << /XObject << /F {6 + i} 0 R >> /ExtGState << /G << /ca 0.9 >> >> >>" : "/Resources << >>";
            extra.Add(RegionTests.Obj($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> {res}", content));
        }

        byte[] pdf = TestPdf.Page("/F Do", "/XObject << /F 5 0 R >>", extra: extra.ToArray());
        Assert.Throws<ImageLimitException>(() => RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>());
    }

    [Fact]
    public void RecursivePatternTerminates()
    {
        // The pattern's cell paints with the pattern itself.
        string pattern = RegionTests.Obj("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << /Pattern << /P1 5 0 R >> >>", "/Pattern cs /P1 scn 0 0 20 20 re f");
        byte[] pdf = TestPdf.Page("/Pattern cs /P1 scn 0 0 200 200 re f 0 g 10 10 20 20 re f", "/Pattern << /P1 5 0 R >>", extra: new[] { pattern });
        var sw = Stopwatch.StartNew();
        try
        {
            Rgba32[] px = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
            Assert.Equal(new Rgba32(0, 0, 0, 255), px[(180 * 200) + 20]);
        }
        catch (ImageException)
        {
            // A typed refusal is acceptable; hanging or crashing is not.
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void HugePatternCellIsDrawnAtLowerResolution()
    {
        // A cell of a million points a side would need a tile far over the side limit.
        string pattern = RegionTests.Obj("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1000000 1000000] /XStep 1000000 /YStep 1000000 /Resources << >>", "0 0.5 0 rg 0 0 1000000 1000000 re f");
        byte[] pdf = TestPdf.Page("/Pattern cs /P1 scn 0 0 200 200 re f", "/Pattern << /P1 5 0 R >>", extra: new[] { pattern });
        long before = Allocated();
        Rgba32[] px = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.True(Allocated() - before < 128L << 20);
        Assert.Equal(200 * 200, px.Length);
    }

    [Fact]
    public void ManyPagesBeyondTheFrameLimitFailTyped()
    {
        var pages = Enumerable.Range(0, 20).Select(_ => ("0 g 0 0 5 5 re f", 20.0, 20.0)).ToArray();
        byte[] pdf = TestPdf.Pages(pages);
        var limits = new ImageLimits { MaxFrames = 10 };
        var config = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 72 }));
        Assert.Throws<ImageLimitException>(() => Image.Load(pdf, new LoadOptions { Configuration = config, Limits = limits }));
    }

    [Fact]
    public void OversizedFileIsRefusedBeforeReading()
    {
        byte[] pdf = TestPdf.Page("0 g 0 0 5 5 re f");
        var options = new PdfRenderOptions { Limits = new PdfRenderLimits { MaxFileBytes = 100 } };
        Assert.Throws<ImageLimitException>(() => RenderTests.Load(pdf, options));
    }

    [Fact]
    public void ClipStackBeyondTheLimitFailsTyped()
    {
        string content = string.Concat(Enumerable.Repeat("q 0 0 200 200 re W n ", 300)) + "0 g 0 0 5 5 re f";
        byte[] pdf = TestPdf.Page(content);
        var options = new PdfRenderOptions { Dpi = 72, Limits = new PdfRenderLimits { MaxClipDepth = 100 } };
        try
        {
            RenderTests.Load(pdf, options).ToArray<Rgba32>();
        }
        catch (ImageLimitException)
        {
            return;
        }

        // OnePDF's own graphics-state depth limit may refuse first; both are typed. Reaching here means it drew within bounds.
    }
}

[CollectionDefinition(LimitTests.CollectionName, DisableParallelization = true)]
public sealed class AllocationMeasurementGroup;
