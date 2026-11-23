using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace OneImage.Pdf.Tests;

/// <summary>
/// A hostile page can make the same or very many pictures cost memory: an inline image inside a stream that is drawn again and again, or a
/// page that shows many large images. These tests build such pages in code and check that drawing them stays bounded (or is refused with
/// <see cref="ImageLimitException"/> when the pictures would not fit the page's budget).
/// </summary>
[Collection(LimitTests.CollectionName)]
public class ImageMemoryTests
{
    /// <summary>A zlib stream of <paramref name="length"/> zero bytes (tiny in the file, large decoded).</summary>
    internal static byte[] Zeros(long length)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] block = new byte[1 << 20];
            for (long left = length; left > 0; left -= block.Length)
            {
                z.Write(block, 0, (int)Math.Min(left, block.Length));
            }
        }

        return ms.ToArray();
    }

    /// <summary>An inline image of <paramref name="side"/> x <paramref name="side"/> gray pixels, Flate-compressed, as content-stream text.</summary>
    internal static string InlineImage(int side) =>
        $"BI /W {side} /H {side} /CS /G /BPC 8 /F /Fl ID\n" + Encoding.Latin1.GetString(Zeros((long)side * side)) + "\nEI\n";

    /// <summary>A page that invokes a form <paramref name="count"/> times; the form draws one inline image.</summary>
    internal static byte[] InlineImageInForm(int count, int side)
    {
        string form = RegionTests.Obj("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "q 20 0 0 20 10 10 cm\n" + InlineImage(side) + "Q");
        return TestPdf.Page(string.Concat(Enumerable.Repeat("/F Do\n", count)), "/XObject << /F 5 0 R >>", extra: new[] { form });
    }

    /// <summary>A page that shows a Type 3 glyph <paramref name="count"/> times; the glyph's procedure draws one inline image.</summary>
    internal static byte[] InlineImageInType3Glyph(int count, int side)
    {
        string font = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 6 0 R >> "
            + "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << >> >>";
        string proc = RegionTests.Obj("", "1000 0 d0 q 1000 0 0 1000 0 0 cm\n" + InlineImage(side) + "Q");
        var content = new StringBuilder("BT /F1 0.01 Tf 10 100 Td\n");
        for (int i = 0; i < count; i += 1000)
        {
            content.Append('(').Append('a', Math.Min(1000, count - i)).Append(") Tj\n");
        }

        content.Append("ET");
        return TestPdf.Page(content.ToString(), "/Font << /F1 5 0 R >>", extra: new[] { font, proc });
    }

    /// <summary>A page that shows <paramref name="count"/> different RGB image XObjects of <paramref name="side"/> x <paramref name="side"/> pixels.</summary>
    internal static byte[] ManyBigImages(int count, int side, string pageExtraImageEntries = "", int draws = 1, bool big = false)
    {
        byte[] data = Zeros((long)side * side * 3);
        var extra = new List<string>();
        var res = new StringBuilder("/XObject << ");
        var content = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            extra.Add(Encoding.Latin1.GetString(TestPdf.Stream(
                $"/Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode {pageExtraImageEntries}", data)));
            res.Append($"/I{i} {5 + i} 0 R ");
            for (int d = 0; d < draws; d++)
            {
                content.Append(big ? $"q 200 0 0 200 0 0 cm /I{i} Do Q\n" : $"q 10 0 0 10 {(i % 10) * 15} {(i / 10) * 15} cm /I{i} Do Q\n");
            }
        }

        res.Append(">>");
        return TestPdf.Page(content.ToString(), res.ToString(), extra: extra.ToArray());
    }

    /// <summary>
    /// Renders <paramref name="pdf"/> while a sampler watches the managed heap, and returns the highest reading above the starting point
    /// together with what the render threw (null when it finished). Garbage that has not been collected yet counts, so the readings are
    /// an upper bound of what was held; a bound that holds with it holds for the live data too. A render that passes
    /// <paramref name="capBytes"/> is cancelled and reported as such.
    /// </summary>
    internal static (long PeakAboveStart, Exception? Failure, bool Capped, TimeSpan Elapsed, long Allocated, long LiveAfter) Measure(byte[] pdf, PdfRenderOptions options, long capBytes = long.MaxValue)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long start = GC.GetTotalMemory(false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        long startLive = GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes;
        long allocStart = GC.GetTotalAllocatedBytes(precise: true);
        long peak = 0;
        bool done = false, capped = false;
        using var cts = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                long above = GC.GetTotalMemory(false) - start;
                if (above > peak)
                {
                    peak = above;
                }

                if (above > capBytes)
                {
                    capped = true;
                    cts.Cancel();
                    return;
                }

                Thread.Sleep(5);
            }
        })
        { IsBackground = true };
        sampler.Start();
        Exception? failure = null;
        var sw = Stopwatch.StartNew();
        Image? image = null;
        try
        {
            image = RenderTests.Load(pdf, options);
            image.ToArray<Rgba32>(cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (capped)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        sw.Stop();
        Volatile.Write(ref done, true);
        sampler.Join();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocStart;

        // What the page still holds with its image alive: bytes that survive a full collection, as the collector counted them while marking.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        long live = GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes - startLive;
        GC.KeepAlive(image);
        return (peak, failure, capped, sw.Elapsed, allocated, live);
    }

    private static readonly PdfRenderOptions Opts = new() { Dpi = 72 };

    [Fact]
    public void InlineImageInAFormDrawnManyTimesIsDecodedOnce()
    {
        // 20,000 invocations of a form holding a 128 x 128 inline image: every run of the form parses a new copy of the image.
        var r = Measure(InlineImageInForm(20000, 128), Opts);
        Assert.Null(r.Failure);
        Assert.True(r.PeakAboveStart < 256L << 20, $"peak {r.PeakAboveStart >> 20} MiB");
        Assert.True(r.LiveAfter < 64L << 20, $"held {r.LiveAfter >> 20} MiB");
    }

    [Fact]
    public void InlineImageInAType3GlyphShownManyTimesIsDecodedOnce()
    {
        var r = Measure(InlineImageInType3Glyph(20000, 128), Opts);
        Assert.Null(r.Failure);
        Assert.True(r.PeakAboveStart < 256L << 20, $"peak {r.PeakAboveStart >> 20} MiB");
        Assert.True(r.LiveAfter < 64L << 20, $"held {r.LiveAfter >> 20} MiB");
    }

    [Fact]
    public void ManyLargeImagesAreNotKeptAfterTheyAreDrawn()
    {
        // 12 different 3000 x 3000 RGB images (27 MB of samples each) on one page. Keeping their decoded data would hold over 300 MB.
        var r = Measure(ManyBigImages(12, 3000), Opts);
        Assert.Null(r.Failure);
        Assert.True(r.LiveAfter < 100L << 20, $"held {r.LiveAfter >> 20} MiB");
    }

    [Fact]
    public void PicturesKeptForThePageStayWithinTheCachedPixelBudget()
    {
        // Nothing may be cached at all: every picture is rebuilt when drawn, and none is held afterwards.
        var options = new PdfRenderOptions { Dpi = 72, Limits = new PdfRenderLimits { MaxCachedPixels = 0 } };
        // Six images would hold 162 MB of samples; what remains is idle pooled buffers (tile and scratch pools keep tens of MB for reuse).
        var r = Measure(ManyBigImages(6, 3000), options);
        Assert.Null(r.Failure);
        Assert.True(r.LiveAfter < 100L << 20, $"held {r.LiveAfter >> 20} MiB");
    }

    [Fact]
    public void ImagesBeyondTheDecodedPixelBudgetFailTyped()
    {
        // Each image is 9 million pixels; the budget allows two of them.
        var options = new PdfRenderOptions { Dpi = 72, Limits = new PdfRenderLimits { MaxDecodedImagePixels = 20_000_000 } };
        var r = Measure(ManyBigImages(12, 3000), options);
        Assert.IsType<ImageLimitException>(r.Failure);
        Assert.True(r.LiveAfter < 100L << 20, $"held {r.LiveAfter >> 20} MiB");

        // The same page within the budget draws.
        var ok = new PdfRenderOptions { Dpi = 72, Limits = new PdfRenderLimits { MaxDecodedImagePixels = 200_000_000 } };
        Assert.Null(Measure(ManyBigImages(12, 3000), ok).Failure);
    }

    [Fact]
    public void AnImageOverTheDecodedStreamLimitIsNotDecodedAgainByEveryCell()
    {
        // The image decodes to 12 MB and the document limit is 4 MB, so it is left out. At 300 dpi the page is 49 cells; each one used to
        // decode the image up to the limit again before giving up.
        var options = new PdfRenderOptions { Dpi = 300, DocumentLimits = new OnePdf.PdfLimits { MaxDecodedStreamBytes = 4L << 20 } };
        var r = Measure(ManyBigImages(1, 2000, draws: 1, big: true), options);
        Assert.Null(r.Failure);
        Assert.True(r.Allocated < 64L << 20, $"allocated {r.Allocated >> 20} MiB");
    }

    [Fact]
    public void InlineImagesNamingAColorSpaceOfTheirResourcesAreNotMixedUp()
    {
        // The same bytes and dictionary mean red in one form and blue in the other, because /Cs1 is looked up in the resources of the form.
        string image = "BI /W 1 /H 1 /BPC 8 /CS /Cs1 ID\n\0\nEI\n";
        string FormWith(string palette) => RegionTests.Obj(
            $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /ColorSpace << /Cs1 [/Indexed /DeviceRGB 1 <{palette}>] >> >>",
            "q 100 0 0 100 0 0 cm\n" + image + "Q");
        byte[] pdf = TestPdf.Page(
            "q 1 0 0 1 0 100 cm /A Do Q q 1 0 0 1 100 100 cm /B Do Q",
            "/XObject << /A 5 0 R /B 6 0 R >>",
            width: 200,
            height: 200,
            extra: new[] { FormWith("FF0000 00FF00"), FormWith("0000FF FFFF00") });
        Rgba32[] px = RenderTests.Load(pdf, Opts).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(50 * 200) + 50]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(50 * 200) + 150]);
    }

    [Fact]
    public void ReducingWhileConvertingEqualsConvertingThenReducing()
    {
        // The same noise drawn at a quarter of its size, once opaque (rows are reduced as they are converted) and once with an all-opaque
        // soft mask (the picture is converted whole, then reduced): the pixels must be the same.
        const int Side = 400;
        byte[] noise = new byte[Side * Side * 3];
        uint x = 12345;
        for (int i = 0; i < noise.Length; i++)
        {
            x = (x * 1664525) + 1013904223;
            noise[i] = (byte)(x >> 24);
        }

        string Image(string extra) => System.Text.Encoding.Latin1.GetString(TestPdf.Deflated(
            $"/Type /XObject /Subtype /Image /Width {Side} /Height {Side} /ColorSpace /DeviceRGB /BitsPerComponent 8 {extra}", noise));
        string mask = System.Text.Encoding.Latin1.GetString(TestPdf.Deflated(
            $"/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 255 }));
        string content = "q 100 0 0 100 20 20 cm /Im Do Q";
        Rgba32[] plain = RenderTests.Load(TestPdf.Page(content, "/XObject << /Im 5 0 R >>", extra: new[] { Image(string.Empty) }), Opts).ToArray<Rgba32>();
        Rgba32[] masked = RenderTests.Load(TestPdf.Page(content, "/XObject << /Im 5 0 R >>", extra: new[] { Image("/SMask 6 0 R"), mask }), Opts).ToArray<Rgba32>();
        Assert.Equal(plain, masked);
        Assert.NotEqual(new Rgba32(255, 255, 255, 255), plain[(100 * 200) + 100]);
    }

    [Fact]
    public void PicturesLargerThanTheCacheBudgetAreBuiltOnceAndShared()
    {
        // A 1200 dpi render of a 360 x 720 pt page (6000 x 12000 px): two 6000 x 6000 images, each shown at its own size (36 Mpx each, 72 Mpx together, over the
        // default 2^26 budget). Each picture used to be rebuilt by every 128 px cell that touched it, and every rebuild counted as a decode.
        byte[] data = Zeros(6000L * 6000 * 3);
        string Image(int n) => System.Text.Encoding.Latin1.GetString(TestPdf.Stream(
            "/Type /XObject /Subtype /Image /Width 6000 /Height 6000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", data));
        byte[] pdf = TestPdf.Page(
            "q 360 0 0 360 0 360 cm /A Do Q q 360 0 0 360 0 0 cm /B Do Q",
            "/XObject << /A 5 0 R /B 6 0 R >>",
            width: 360,
            height: 720,
            extra: new[] { Image(0), Image(1) });
        long before = OneImage.Pdf.Render.PageRender.DecodeCount;
        var r = Measure(pdf, new PdfRenderOptions { Dpi = 1200 });
        Assert.Null(r.Failure);
        Assert.True(OneImage.Pdf.Render.PageRender.DecodeCount - before <= 2, $"{OneImage.Pdf.Render.PageRender.DecodeCount - before} decodes");
        Assert.True(r.PeakAboveStart < 2000L << 20, $"peak {r.PeakAboveStart >> 20} MiB");
        // The two kept pictures are most of the budget (2^26 pixels of 4 bytes is 256 MiB), plus idle pooled buffers.
        TestContext.Current.SendDiagnosticMessage($"large pictures: peak {r.PeakAboveStart >> 20} MiB, held {r.LiveAfter >> 20} MiB, {OneImage.Pdf.Render.PageRender.DecodeCount - before} decodes, {r.Elapsed.TotalSeconds:F1} s");
        Assert.True(r.LiveAfter < 400L << 20, $"held {r.LiveAfter >> 20} MiB");
    }

    [Fact]
    public void ConcurrentReadsEachCountTheirOwnDecodedPixels()
    {
        // Each read of the bottom cells decodes 3 images of 9 Mpx = 27 Mpx (nothing is cached); the limit allows one read but not two.
        // Reads on several threads must each pass, which they only do when the count is not shared between them.
        byte[] pdf = ManyBigImages(3, 3000);
        var options = new PdfRenderOptions { Dpi = 72, Limits = new PdfRenderLimits { MaxDecodedImagePixels = 30_000_000, MaxCachedPixels = 0 } };
        Image image = RenderTests.Load(pdf, options);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try
            {
                Rgba32[] row = new Rgba32[200];
                image.CopyPixelsTo(row, new ImageRect(0, 150 + i, 200, 1));
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });
        Assert.Empty(failures);
    }
}
