using System.Buffers.Binary;
using System.Numerics;
using static OneImage.Metafile.Tests.EmfPlusBuilder;

namespace OneImage.Metafile.Tests;

/// <summary>EMF+ records, built from the layouts of [MS-EMFPLUS] and rendered without anti-aliasing so that pixels can be checked exactly.</summary>
public class EmfPlusTests
{
    private static readonly uint Red = Argb(255, 255, 0, 0);
    private static readonly uint Green = Argb(255, 0, 200, 0);
    private static readonly uint Blue = Argb(255, 0, 0, 255);
    private static readonly uint Black = Argb(255, 0, 0, 0);

    private static (Rgba32[] P, int W, int H) Render(byte[] emf) => MetafileRenderTests.Render(emf, fonts: MetafileRenderTests.TestFonts());

    private static Rgba32 At((Rgba32[] P, int W, int H) r, int x, int y) => r.P[(y * r.W) + x];

    private static void Is(Rgba32 c, byte r, byte g, byte b, byte a = 255) => Assert.Equal(new Rgba32(r, g, b, a), c);

    private static void Clear(Rgba32 c) => Assert.Equal(0, c.A);

    private static void Near(Rgba32 c, int r, int g, int b, int a, int tolerance = 3)
    {
        Assert.True(Math.Abs(c.R - r) <= tolerance && Math.Abs(c.G - g) <= tolerance && Math.Abs(c.B - b) <= tolerance && Math.Abs(c.A - a) <= tolerance, $"{c} is not near ({r},{g},{b},{a})");
    }

    private static int Dark((Rgba32[] P, int W, int H) r, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                Rgba32 c = At(r, x, y);
                if (c.A > 128 && c.R < 100 && c.G < 100 && c.B < 100)
                {
                    n++;
                }
            }
        }

        return n;
    }

    // ---- fills and strokes ---------------------------------------------------------------------------------------------

    [Fact]
    public void FillRectsWithAnArgbColorIsExactAndAlphaBlends()
    {
        var r = Render(new EmfPlusBuilder().FillRects(Red, (10, 10, 40, 30)).FillRects(Argb(128, 0, 0, 255), (30, 20, 40, 40)).Build());
        Is(At(r, 20, 15), 255, 0, 0);
        Clear(At(r, 9, 15));
        Clear(At(r, 50, 5));
        Near(At(r, 40, 25), 127, 0, 128, 255);
        Near(At(r, 60, 50), 0, 0, 255, 128);
    }

    [Fact]
    public void BrushObjectsAreUsedByIndex()
    {
        var r = Render(new EmfPlusBuilder().SolidBrush(3, Green).FillRectsBrush(3, (0, 0, 20, 20)).Build());
        Is(At(r, 5, 5), 0, 200, 0);
    }

    [Fact]
    public void DrawRectsStrokesWithThePensWidthCenteredOnTheEdge()
    {
        var r = Render(new EmfPlusBuilder().Pen(1, 4, Blue).DrawRects(1, (20, 20, 50, 50)).Build());
        Is(At(r, 19, 40), 0, 0, 255);
        Is(At(r, 21, 40), 0, 0, 255);
        Clear(At(r, 40, 40));
        Clear(At(r, 17, 40));
    }

    [Fact]
    public void PolygonsEllipsesAndClosedLinesFill()
    {
        var r = Render(new EmfPlusBuilder().FillPolygon(Red, (10, 10), (90, 10), (50, 60)).FillEllipse(Green, 20, 65, 60, 30).Build());
        Is(At(r, 50, 30), 255, 0, 0);
        Clear(At(r, 15, 50));
        Is(At(r, 50, 80), 0, 200, 0);
        Clear(At(r, 22, 67));
        var lines = Render(new EmfPlusBuilder().Pen(1, 3, Black).DrawLines(1, true, (10, 10), (80, 10), (80, 80)).Build());
        Is(At(lines, 45, 10), 0, 0, 0);
        Is(At(lines, 80, 45), 0, 0, 0);
        Is(At(lines, 45, 46), 0, 0, 0); // the closing line runs from (80,80) back to (10,10)
    }

    [Fact]
    public void PathFillModeFollowsTheObjectAndTheCurvesPassThroughTheirPoints()
    {
        (float, float, byte)[] star = [(50, 5, 0), (80, 95, 1), (5, 35, 1), (95, 35, 1), (20, 95, 0x81)];
        var winding = Render(new EmfPlusBuilder().Path(1, star, winding: true).FillPath(Red, 1).Build());
        var alternate = Render(new EmfPlusBuilder().Path(1, star).FillPath(Red, 1).Build());
        Is(At(winding, 50, 50), 255, 0, 0);
        Clear(At(alternate, 50, 50));
        Is(At(alternate, 50, 20), 255, 0, 0);

        // A cardinal spline (DrawCurve) goes through every point it is given.
        var pts = new Data().F32(0.5f).U32(0).U32(2).U32(3).I16(10).I16(50).I16(50).I16(20).I16(90).I16(50);
        var curve = Render(new EmfPlusBuilder().Pen(1, 3, Black).Record(0x4018, 0x4001, pts).Build());
        Assert.True(Dark(curve, 8, 48, 13, 53) > 0);
        Assert.True(Dark(curve, 48, 17, 53, 23) > 0);
        Assert.True(Dark(curve, 87, 48, 92, 53) > 0);
    }

    [Fact]
    public void PieAndArcFollowTheRayAngleOnAnEllipse()
    {
        // A pie from 0 to 90 degrees (clockwise, y down) of a 80 x 40 ellipse centered at (50,50) is its lower right quarter.
        var d = new Data().F32(0).F32(90).F32(10).F32(30).F32(80).F32(40);
        var r = Render(new EmfPlusBuilder().Record(0x4010, 0x8000, new Data().U32(Red).Append(d)).Build());
        Is(At(r, 70, 60), 255, 0, 0);
        Clear(At(r, 30, 60));
        Clear(At(r, 70, 40));
    }

    // ---- regions and clips ---------------------------------------------------------------------------------------------

    private static Data XorRegion() =>
        new Data().U32(0xDBC01002).U32(2).U32(3)
            .U32(0x10000000).F32(10).F32(10).F32(50).F32(50)
            .U32(0x10000000).F32(40).F32(40).F32(50).F32(50);

    [Fact]
    public void RegionTreesCombineAndFill()
    {
        var b = new EmfPlusBuilder().Object(4, 1, XorRegion()).Record(0x4013, 0x8001, new Data().U32(Red));
        var r = Render(b.Build());
        Is(At(r, 20, 20), 255, 0, 0);
        Clear(At(r, 50, 50)); // in both rectangles, so not in the xor
        Is(At(r, 80, 80), 255, 0, 0);
        Clear(At(r, 80, 15));
    }

    [Fact]
    public void ClipRectsReplaceIntersectAndExclude()
    {
        var r = Render(new EmfPlusBuilder().SetClipRect(0, 20, 20, 50, 50).FillRects(Red, (0, 0, 100, 100)).Build());
        Is(At(r, 30, 30), 255, 0, 0);
        Clear(At(r, 10, 10));
        Clear(At(r, 75, 75));
        var ex = Render(new EmfPlusBuilder().SetClipRect(4, 20, 20, 50, 50).FillRects(Red, (0, 0, 100, 100)).Build());
        Clear(At(ex, 30, 30));
        Is(At(ex, 10, 10), 255, 0, 0);
        var inter = Render(new EmfPlusBuilder().SetClipRect(0, 20, 20, 50, 50).SetClipRect(1, 40, 40, 50, 50).FillRects(Red, (0, 0, 100, 100)).Build());
        Is(At(inter, 50, 50), 255, 0, 0);
        Clear(At(inter, 30, 30));
        var reset = Render(new EmfPlusBuilder().SetClipRect(0, 20, 20, 5, 5).Record(0x4031, 0, []).FillRects(Red, (0, 0, 100, 100)).Build());
        Is(At(reset, 90, 90), 255, 0, 0);
    }

    [Fact]
    public void ClipFollowsTheWorldTransformInForceWhenItIsSet()
    {
        var r = Render(new EmfPlusBuilder().Translate(30, 0).SetClipRect(0, 0, 0, 20, 20).Translate(-30, 0).FillRects(Red, (0, 0, 100, 100)).Build());
        Is(At(r, 40, 10), 255, 0, 0);
        Clear(At(r, 10, 10));
    }

    // ---- transforms and state ------------------------------------------------------------------------------------------

    [Fact]
    public void WorldTransformsApplyInTheirOrder()
    {
        var t = Render(new EmfPlusBuilder().Translate(20, 10).FillRects(Red, (0, 0, 10, 10)).Build());
        Is(At(t, 25, 15), 255, 0, 0);
        Clear(At(t, 5, 5));
        var s = Render(new EmfPlusBuilder().Scale(2, 2).FillRects(Red, (10, 10, 10, 10)).Build());
        Is(At(s, 30, 30), 255, 0, 0);
        Clear(At(s, 15, 15));
        // Translate first, then rotate by pre-multiplication: the rectangle turns about the origin, then moves.
        var rot = Render(new EmfPlusBuilder().Translate(50, 0).Rotate(90).FillRects(Red, (10, 0, 10, 10)).Build());
        Is(At(rot, 45, 15), 255, 0, 0);
        Clear(At(rot, 65, 5));
    }

    [Fact]
    public void PostMultiplyAppliesTheMatrixAfterTheWorldTransform()
    {
        var r = Render(new EmfPlusBuilder().Scale(2, 2).Translate(10, 0, post: true).FillRects(Red, (0, 0, 10, 10)).Build());
        Is(At(r, 25, 5), 255, 0, 0); // scaled to 0..20, then moved 10 right
        Clear(At(r, 5, 5));
    }

    [Fact]
    public void SaveRestoreAndContainersRestoreTheTransform()
    {
        var r = Render(new EmfPlusBuilder().Save(1).Translate(50, 0).Restore(1).FillRects(Red, (0, 0, 10, 10)).Build());
        Is(At(r, 5, 5), 255, 0, 0);
        Clear(At(r, 55, 5));

        // A container maps its source rectangle onto its destination rectangle.
        var d = new Data().F32(50).F32(50).F32(50).F32(50).F32(0).F32(0).F32(10).F32(10).U32(7);
        var c = Render(new EmfPlusBuilder().Record(0x4027, 2, d).FillRects(Green, (0, 0, 10, 10)).Record(0x4029, 0, new Data().U32(7)).FillRects(Red, (0, 0, 10, 10)).Build());
        Is(At(c, 75, 75), 0, 200, 0);
        Is(At(c, 5, 5), 255, 0, 0);
        Clear(At(c, 25, 25));
    }

    [Fact]
    public void PageUnitsScaleWithTheHeaderDpi()
    {
        // One inch at 96 dpi is 96 pixels.
        var r = Render(new EmfPlusBuilder().Record(0x4030, 4, new Data().F32(1)).FillRects(Red, (0, 0, 1, 1)).Build());
        Is(At(r, 90, 90), 255, 0, 0);
        Clear(At(r, 98, 98));
    }

    [Fact]
    public void ClearReplacesWhatIsThere()
    {
        var r = Render(new EmfPlusBuilder().FillRects(Red, (0, 0, 50, 50)).Clear(Argb(255, 0, 128, 0)).Build());
        Is(At(r, 10, 10), 0, 128, 0);
        Is(At(r, 99, 99), 0, 128, 0);
    }

    [Fact]
    public void SourceCopyCompositingReplacesTheDestination()
    {
        var r = Render(new EmfPlusBuilder().FillRects(Red, (0, 0, 50, 50)).Record(0x4023, 1, []).FillRects(Argb(128, 0, 0, 255), (20, 20, 50, 50)).Build());
        Near(At(r, 30, 30), 0, 0, 255, 128);
    }

    // ---- brushes -------------------------------------------------------------------------------------------------------

    [Fact]
    public void LinearGradientBrushRunsAlongTheRectangle()
    {
        var r = Render(new EmfPlusBuilder().LinearGradient(1, 0, 0, 100, 10, Red, Blue).FillRectsBrush(1, (0, 0, 100, 10)).Build());
        Assert.True(At(r, 3, 5).R > 230 && At(r, 3, 5).B < 25);
        Assert.True(At(r, 96, 5).B > 230 && At(r, 96, 5).R < 25);
        Assert.InRange(At(r, 50, 5).R, 110, 145);
    }

    [Fact]
    public void HatchBrushesPaintTheirForegroundAndBackground()
    {
        var r = Render(new EmfPlusBuilder().HatchBrush(1, 12, Red, Blue).FillRectsBrush(1, (0, 0, 40, 40)).Build());
        int red = 0, blue = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Rgba32 c = At(r, x, y);
                red += c is { R: 255, B: 0 } ? 1 : 0;
                blue += c is { R: 0, B: 255 } ? 1 : 0;
            }
        }

        Assert.Equal(128, red);
        Assert.Equal(128, blue);
    }

    // ---- images --------------------------------------------------------------------------------------------------------

    private static Data DrawImageData(float sw, float sh, int x, int y, int w, int h) =>
        new Data().U32(0xFFFFFFFF).U32(2).F32(0).F32(0).F32(sw).F32(sh).Append(Rects16((x, y, w, h)));

    [Fact]
    public void BitmapsDrawIntoTheirRectangleWithNearestOrSmoothSampling()
    {
        uint[] px = [Red, Green, Blue, Argb(255, 255, 255, 255)];
        var b = new EmfPlusBuilder().Bitmap(2, 2, 2, (x, y) => px[(y * 2) + x]).Record(0x4021, 5, []).Record(0x401A, 0x4002, DrawImageData(2, 2, 10, 10, 40, 40));
        var r = Render(b.Build());
        Is(At(r, 15, 15), 255, 0, 0);
        Is(At(r, 45, 15), 0, 200, 0);
        Is(At(r, 15, 45), 0, 0, 255);
        Is(At(r, 45, 45), 255, 255, 255);
        Clear(At(r, 5, 5));
        var smooth = new EmfPlusBuilder().Bitmap(2, 2, 2, (x, y) => px[(y * 2) + x]).Record(0x401A, 0x4002, DrawImageData(2, 2, 10, 10, 40, 40));
        var s = Render(smooth.Build());
        Assert.NotEqual(At(s, 30, 15), At(s, 15, 15));
    }

    [Fact]
    public void ImagePointsPlaceTheBitmapInAParallelogram()
    {
        // Three points: top left, top right, bottom left. Here the image is turned onto its side.
        var d = new Data().U32(0xFFFFFFFF).U32(2).F32(0).F32(0).F32(2).F32(2).U32(3).I16(60).I16(10).I16(60).I16(60).I16(10).I16(10);
        var b = new EmfPlusBuilder().Bitmap(2, 2, 2, (x, y) => x == 0 && y == 0 ? Red : Blue).Record(0x4021, 5, []).Record(0x401B, 0x4002, d);
        var r = Render(b.Build());
        Is(At(r, 55, 15), 255, 0, 0); // the red top left pixel is at the top right of the destination
        Is(At(r, 15, 15), 0, 0, 255);
    }

    [Fact]
    public void MetafileImagesPlayAsCommandsInsideTheirDestination()
    {
        byte[] inner = new EmfBuilder().CreateBrush(1, 0, EmfBuilder.Rgb(255, 0, 0)).Select(1).Rectangle(0, 0, 100, 100).Build();
        var img = new Data().U32(0xDBC01002).U32(2).U32(3).U32((uint)inner.Length).Bytes(inner);
        var r = Render(new EmfPlusBuilder().Object(5, 1, img).Record(0x401A, 0x4001, DrawImageData(100, 100, 50, 50, 40, 40)).Build());
        Is(At(r, 70, 70), 255, 0, 0);
        Clear(At(r, 20, 20));
        Clear(At(r, 95, 95));
    }

    // ---- text ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void StringsAndDriverStringsDrawGlyphs()
    {
        var str = new Data().U32(Black).U32(0xFFFFFFFF).U32(5).F32(10).F32(10).F32(0).F32(0).Bytes(System.Text.Encoding.Unicode.GetBytes("Hello"));
        var r = Render(new EmfPlusBuilder().Font(1, "Arial", 24).Record(0x401C, 0x8001, str).Build());
        Assert.True(Dark(r, 10, 10, 90, 45) > 40);
        Assert.Equal(0, Dark(r, 0, 60, 100, 100));

        byte[] glyphs = System.Text.Encoding.Unicode.GetBytes("HI");
        var drv = new Data().U32(Black).U32(1).U32(0).U32(2).Bytes(glyphs).F32(10).F32(80).F32(40).F32(80);
        var d = Render(new EmfPlusBuilder().Font(1, "Arial", 24).Record(0x4036, 0x8001, drv).Build());
        Assert.True(Dark(d, 8, 55, 70, 85) > 30);
        Assert.Equal(0, Dark(d, 0, 0, 100, 45));
    }

    // ---- dual files, GetDC and the command stream -----------------------------------------------------------------------

    [Fact]
    public void InADualFileTheEmfPlusRecordsWinAndTheGdiRecordsAreNotDrawnTwice()
    {
        var b = new EmfPlusBuilder(100, dual: true).FillRects(Red, (10, 10, 30, 30)).Flush();
        // The GDI fallback of the same drawing, in a different color, and an extra shape only the fallback has.
        b.Gdi.CreateBrush(1, 0, EmfBuilder.Rgb(0, 0, 255)).Select(1).Rectangle(10, 10, 40, 40).Rectangle(60, 60, 90, 90);
        var r = Render(b.Build());
        Is(At(r, 20, 20), 255, 0, 0);
        Clear(At(r, 70, 70));
    }

    [Fact]
    public void GdiRecordsPlayInsideAGetDcSectionAndTheNextEmfPlusRecordEndsIt()
    {
        var b = new EmfPlusBuilder().Translate(10, 0).Record(0x4004, 0, []).Flush();
        b.Gdi.CreateBrush(1, 0, EmfBuilder.Rgb(0, 200, 0)).Select(1).Rectangle(50, 50, 80, 80);
        b.FillRects(Red, (0, 0, 10, 10));
        var r = Render(b.Build());
        Is(At(r, 70, 70), 0, 200, 0); // the section starts in the EMF+ world, which was moved 10 right
        Is(At(r, 15, 5), 255, 0, 0);
    }

    [Fact]
    public void ObjectsCanBeSplitAcrossRecords()
    {
        byte[] whole = new Data().U32(0xDBC01002).U32(0).U32(Green).ToArray();
        var b = new EmfPlusBuilder()
            .Record(0x4008, (ushort)(0x8000 | (1 << 8) | 4), new Data().U32(12).Bytes(whole[..8]))
            .Record(0x4008, (ushort)((1 << 8) | 4), new Data().Bytes(whole[8..]))
            .FillRectsBrush(4, (0, 0, 20, 20));
        Is(At(Render(b.Build()), 5, 5), 0, 200, 0);
    }

    [Fact]
    public void TheCommandStreamIsResolvedAndBalanced()
    {
        byte[] emf = new EmfPlusBuilder().Save(1).Translate(10, 20).SolidBrush(2, Argb(128, 1, 2, 3)).FillRectsBrush(2, (0, 0, 5, 5)).Restore(1).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf);
        Assert.True(doc.Info.HasEmfPlus);
        var fill = doc.Commands.OfType<FillPathCommand>().Single();
        Assert.Equal(new SolidBrushSpec(new Rgba32(1, 2, 3, 128)), fill.Brush);
        Assert.Equal(new RectD(0, 0, 5, 5), fill.Path.Bounds);
        SetTransformCommand t = doc.Commands.OfType<SetTransformCommand>().Single();
        Assert.Equal(new Matrix3x2(1, 0, 0, 1, 10, 20), t.Transform);
        Assert.Single(doc.Commands.OfType<SaveStateCommand>());
        Assert.Single(doc.Commands.OfType<RestoreStateCommand>());
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
    }

    [Fact]
    public void UnfinishedSavesAreClosedAtTheEnd()
    {
        MetafileDocument doc = MetafileDocument.Parse(new EmfPlusBuilder().Save(1).Save(2).Build());
        Assert.Equal(2, doc.Commands.OfType<SaveStateCommand>().Count());
        Assert.Equal(2, doc.Commands.OfType<RestoreStateCommand>().Count());
    }

    [Fact]
    public void UnknownAndUnsupportedRecordsAreReported()
    {
        MetafileDocument doc = MetafileDocument.Parse(new EmfPlusBuilder().Record(0x4077, 0, []).Record(0x4037, 0, new Data().U32(1)).Record(0x4026, 0, new Data().U32(9)).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("0x4077", StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("StrokeFillPath", StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("restore of state 9", StringComparison.Ordinal));
    }

    // ---- hostile input -------------------------------------------------------------------------------------------------

    private static MetafileDocument Parse(byte[] emf, MetafileLimits? limits = null) =>
        MetafileDocument.Parse(emf, limits is null ? null : new MetafileReadOptions { Limits = limits });

    [Fact]
    public void AContinuedObjectOverTheLimitIsRefusedBeforeAnythingIsAllocated()
    {
        byte[] emf = new EmfPlusBuilder().Record(0x4008, (ushort)(0x8000 | (1 << 8) | 1), new Data().U32(int.MaxValue).U32(1).U32(2)).Build();
        MetafileDocument doc = Parse(emf);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("more than the limit", StringComparison.Ordinal));
        MetafileDocument small = Parse(new EmfPlusBuilder().Record(0x4008, (ushort)(0x8000 | (1 << 8) | 1), new Data().U32(5000).U32(1)).Build(), new MetafileLimits { MaxPlusObjectBytes = 1000 });
        Assert.Contains(small.Diagnostics, d => d.Message.Contains("more than the limit", StringComparison.Ordinal));
    }

    [Fact]
    public void CountsLargerThanTheirDataAreDamagedObjectsNotAllocations()
    {
        // A path of two billion points, a region of two billion nodes, a bitmap of 100000 x 100000 pixels and a rectangle list of the same size.
        var path = new Data().U32(0xDBC01002).U32(0x7FFFFFFF).U32(0).U32(0);
        var region = new Data().U32(0xDBC01002).U32(0x7FFFFFF0);
        var bitmap = new Data().U32(0xDBC01002).U32(1).U32(100000).U32(100000).U32(400000).U32(0x0026200A).U32(0).U32(0);
        MetafileDocument doc = Parse(new EmfPlusBuilder()
            .Object(3, 1, path).Object(4, 2, region).Object(5, 3, bitmap)
            .Record(0x400A, 0xC000, new Data().U32(Red).U32(0x7FFFFFF0)).Build());
        Assert.Equal(4, doc.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("damaged", StringComparison.Ordinal)));
        Assert.Empty(doc.Commands.OfType<FillPathCommand>());
    }

    [Fact]
    public void ImpossibleRecordSizesStopTheCommentWithoutReadingPastIt()
    {
        var rec = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(rec, 0x400A);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), 4000);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), 3988);
        MetafileDocument doc = Parse(new EmfPlusBuilder().Record(0x4009, 0, new Data().U32(Red)).Flush().Gdi.Comment("EMF+", rec).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("impossible size", StringComparison.Ordinal));
    }

    [Fact]
    public void SavedStatesAreLimited()
    {
        var b = new EmfPlusBuilder();
        for (uint i = 0; i < 40; i++)
        {
            b.Save(i);
        }

        Assert.Throws<ImageLimitException>(() => Parse(b.Build(), new MetafileLimits { MaxSaveDepth = 16 }));
    }

    [Fact]
    public void NestedMetafilesStopAtTheDepthLimit()
    {
        byte[] file = new EmfBuilder().CreateBrush(1, 0, EmfBuilder.Rgb(255, 0, 0)).Select(1).Rectangle(0, 0, 100, 100).Build();
        for (int level = 0; level < 7; level++)
        {
            var img = new Data().U32(0xDBC01002).U32(2).U32(3).U32((uint)file.Length).Bytes(file);
            file = new EmfPlusBuilder().Object(5, 1, img).Record(0x401A, 0x4001, DrawImageData(100, 100, 0, 0, 100, 100)).Build();
        }

        MetafileDocument doc = Parse(file);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("nest deeper than 4", StringComparison.Ordinal));
        Assert.True(doc.Commands.Count < 200);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsMetafilesInImagesPlayThroughTheWmfPath(bool placeable)
    {
        WmfBuilder wmf = new WmfBuilder();
        if (placeable)
        {
            wmf.Placeable(0, 0, 100, 100);
        }
        else
        {
            wmf.SetWindowOrg(0, 0).SetWindowExt(100, 100);
        }

        byte[] file = wmf.CreateBrush(0, WmfBuilder.Rgb(255, 0, 0)).Select(0).Rectangle(0, 0, 100, 100).Build();
        var img = new Data().U32(0xDBC01002).U32(2).U32(placeable ? 2u : 1u).U32((uint)file.Length).Bytes(file);
        byte[] emf = new EmfPlusBuilder().Object(5, 1, img).Record(0x401A, 0x4001, DrawImageData(100, 100, 20, 20, 40, 40)).Build();
        MetafileDocument doc = Parse(emf);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Message.Contains("not supported", StringComparison.Ordinal));
        Assert.NotEmpty(doc.Commands.OfType<FillPathCommand>());
        var r = Render(emf);
        Is(At(r, 40, 40), 255, 0, 0);
        Clear(At(r, 10, 10));
        Clear(At(r, 70, 70));
    }

    [Fact]
    public void ADamagedWindowsMetafileInAnImageIsSkippedWithAWarning()
    {
        var img = new Data().U32(0xDBC01002).U32(2).U32(1).U32(12).Bytes(new byte[12]);
        MetafileDocument doc = Parse(new EmfPlusBuilder().Object(5, 1, img).Record(0x401A, 0x4001, DrawImageData(100, 100, 20, 20, 40, 40)).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("embedded metafile cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongPointCountsAndMissingObjectsAreReportedAndSkipped()
    {
        var d = new Data().U32(0xFFFFFFFF).U32(2).F32(0).F32(0).F32(2).F32(2).U32(2).I16(0).I16(0).I16(1).I16(1);
        MetafileDocument doc = Parse(new EmfPlusBuilder().Bitmap(1, 2, 2, (_, _) => Red).Record(0x401B, 0x4001, d)
            .Record(0x4014, 0x8009, new Data().U32(Red)).Record(0x400B, 0x4005, new Data().U32(1).Append(Rects16((0, 0, 1, 1)))).Build());
        Assert.Contains(doc.Diagnostics, x => x.Message.Contains("three points", StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, x => x.Message.Contains("path object that does not exist", StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, x => x.Message.Contains("pen 5", StringComparison.Ordinal));
        Assert.Empty(doc.Commands.OfType<DrawImageCommand>());
    }

    [Fact]
    public void RelativePointsAreReadAsRunningSums()
    {
        // Relative 7-bit coordinates: (10,20), then +5,+5, then -3,+7.
        var pts = new Data().U32(3).U8(10).U8(20).U8(5).U8(5).U8(0x7D).U8(7); // 0x7D is -3 as a 7-bit integer
        MetafileDocument doc = Parse(new EmfPlusBuilder().SolidBrush(1, Red).Record(0x400C, 0x0800, new Data().U32(1).Append(pts)).Build());
        FillPathCommand fill = doc.Commands.OfType<FillPathCommand>().Single();
        Assert.Equal(new Vector2(12, 32), fill.Path.Points[2]);
        Assert.Equal(new Vector2(15, 25), fill.Path.Points[1]);
    }
}
