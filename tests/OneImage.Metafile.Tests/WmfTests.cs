using System.Buffers.Binary;

namespace OneImage.Metafile.Tests;

/// <summary>Windows metafile reading: sizes and sniffing, then each record family through the pixels or commands it produces, then damaged and hostile files.</summary>
public sealed class WmfTests
{
    private static readonly int Red = WmfBuilder.Rgb(255, 0, 0);
    private static readonly int Green = WmfBuilder.Rgb(0, 255, 0);
    private static readonly int Blue = WmfBuilder.Rgb(0, 0, 255);

    private static WmfBuilder Picture(int size = 100) => new WmfBuilder().Placeable(0, 0, size, size).CreatePen(5, 0, 0);

    /// <summary>A placeable picture with a null pen in slot 0, selected, so fills show their exact extent.</summary>
    private static WmfBuilder Fill(int color, int size = 100) => Picture(size).CreateBrush(0, color).Select(0).Select(1);

    private static (Rgba32[] P, int W, int H) Render(byte[] wmf, int? width = null, bool antialias = false, MetafileFontOptions? fonts = null, MetafileLimits? limits = null)
    {
        var config = new ImageConfiguration().Add(new WmfFormat(limits: limits, fonts: fonts, antialias: antialias));
        Image img = Image.Load(wmf, new LoadOptions { Configuration = config });
        if (width is { } w)
        {
            img = img.Resize(width: w);
        }

        return (img.ToArray<Rgba32>(), img.Width, img.Height);
    }

    private static Rgba32 At((Rgba32[] P, int W, int H) r, int x, int y) => r.P[(y * r.W) + x];

    private static void Is(Rgba32 c, int rgb) => Assert.Equal(new Rgba32((byte)(rgb & 255), (byte)((rgb >> 8) & 255), (byte)((rgb >> 16) & 255), 255), c);

    private static void Clear(Rgba32 c) => Assert.Equal(0, c.A);

    private static MetafileDocument Parse(byte[] wmf, MetafileReadOptions? options = null) => MetafileDocument.Parse(wmf, options);

    private static MetafileReadOptions Limited(MetafileLimits limits) => new() { Limits = limits };

    // ---- sniffing and size -------------------------------------------------------------------------------------------

    [Fact]
    public void BothHeaderFormsAreRecognizedAndEmfIsNot()
    {
        byte[] placeable = Fill(Red).Build();
        byte[] plain = new WmfBuilder().SetWindowExt(10, 10).Build();
        Assert.True(MetafileDocument.IsWmf(placeable));
        Assert.True(MetafileDocument.IsWmf(plain));
        Assert.True(new WmfFormat().Matches(placeable));
        Assert.False(new MetafileFormat().Matches(placeable));
        byte[] emf = new EmfBuilder().Rectangle(1, 1, 5, 5).Build();
        Assert.False(MetafileDocument.IsWmf(emf));
        Assert.False(new WmfFormat().Matches(emf));
        Assert.False(MetafileDocument.IsWmf(new byte[40]));
    }

    [Fact]
    public void PlaceableBoundingBoxAndUnitsPerInchGiveThePixelSize()
    {
        // 1440 x 720 twips at 1440 per inch is 1 x 0.5 inch: 96 x 48 pixels of a 96 dpi display.
        byte[] wmf = new WmfBuilder().Placeable(0, 0, 1440, 720, 1440).Build();
        ImageInfo info = Image.Identify(wmf, new LoadOptions { Configuration = new ImageConfiguration().Add(WmfFormat.Instance) });
        Assert.Equal((96, 48), (info.Width, info.Height));
        Assert.Equal("wmf", info.Format.Name);
        MetafileInfo mi = MetafileDocument.Identify(wmf);
        Assert.Equal(MetafileKind.Wmf, mi.Kind);
        Assert.Equal(96, mi.Width, 6);
    }

    [Fact]
    public void PlaceableBoundingBoxNeedNotStartAtZero()
    {
        byte[] wmf = new WmfBuilder().Placeable(-50, 20, 150, 120).CreatePen(5, 0, 0).CreateBrush(0, Red).Select(0).Select(1).Rectangle(-50, 20, 0, 70).Build();
        var r = Render(wmf);
        Assert.Equal((200, 100), (r.W, r.H));
        Is(At(r, 10, 10), Red);
        Clear(At(r, 60, 10));
    }

    [Fact]
    public void WithoutThePlaceableHeaderTheWindowExtentIsThePictureInPixels()
    {
        byte[] wmf = new WmfBuilder().SetMapMode(8).SetWindowOrg(0, 0).SetWindowExt(200, 80).CreatePen(5, 0, 0).CreateBrush(0, Red).Select(0).Select(1).Rectangle(0, 0, 100, 40).Build();
        var r = Render(wmf);
        Assert.Equal((200, 80), (r.W, r.H));
        Is(At(r, 50, 20), Red);
        Clear(At(r, 150, 20));
    }

    [Fact]
    public void WithoutAWindowTheDrawnExtentIsUsed()
    {
        byte[] wmf = new WmfBuilder().CreatePen(5, 0, 0).CreateBrush(0, Red).Select(0).Select(1).Rectangle(10, 10, 60, 40).Build();
        var r = Render(wmf);
        Assert.Equal((50, 30), (r.W, r.H));
        Is(At(r, 25, 15), Red);
    }

    [Fact]
    public void NotAWmfOrCutShortIsAFormatError()
    {
        Assert.Throws<ImageFormatException>(() => MetafileDocument.Parse(new byte[] { 1, 0, 9, 0, 0, 3 }));
        Assert.Throws<ImageFormatException>(() => MetafileDocument.Parse(new byte[30]));
        byte[] valid = Fill(Red).Build();
        Assert.Throws<ImageFormatException>(() => MetafileDocument.Parse(valid.AsMemory(0, 25)));
    }

    // ---- objects and state -------------------------------------------------------------------------------------------

    [Fact]
    public void RectangleIsRightAndBottomExclusive()
    {
        var r = Render(Fill(Red).Rectangle(10, 10, 50, 40).Build());
        Is(At(r, 10, 10), Red);
        Is(At(r, 49, 39), Red);
        Clear(At(r, 50, 25));
        Clear(At(r, 30, 40));
        Clear(At(r, 9, 25));
    }

    [Fact]
    public void AFreedObjectSlotIsReusedByTheNextCreate()
    {
        // Slots: pen 0, brush red 1, brush green 2. Deleting 1 frees it: the next brush (blue) takes slot 1 again, and selecting 1 selects it.
        byte[] wmf = Picture().CreateBrush(0, Red).CreateBrush(0, Green).Delete(1).CreateBrush(0, Blue).Select(0).Select(1).Rectangle(10, 10, 30, 30).Build();
        Is(At(Render(wmf), 20, 20), Blue);
    }

    [Fact]
    public void ASlotCountsPaletteAndRegionObjectsToo()
    {
        // pen 0, palette 1, region 2: the brush is slot 3.
        byte[] wmf = Picture().CreatePalette().CreateRegion((0, 0, 5, 5)).CreateBrush(0, Red).Select(0).Select(3).Rectangle(10, 10, 30, 30).Build();
        Is(At(Render(wmf), 20, 20), Red);
    }

    [Fact]
    public void SelectingAMissingObjectIsReportedAndIgnored()
    {
        MetafileDocument doc = Parse(Picture().Select(9).Delete(7).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("object 9"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("object 7"));
    }

    [Fact]
    public void SaveAndRestoreBringBackThePenAndBrush()
    {
        byte[] wmf = Picture().CreateBrush(0, Red).CreateBrush(0, Blue).Select(0).Select(1).SaveDc().Select(2).Rectangle(0, 0, 20, 20).RestoreDc(-1).Rectangle(30, 0, 50, 20).Build();
        var r = Render(wmf);
        Is(At(r, 10, 10), Blue);
        Is(At(r, 40, 10), Red);
    }

    [Fact]
    public void OffsetWindowOriginShiftsTheDrawing()
    {
        var r = Render(Fill(Red).OffsetWindowOrg(10, 0).Rectangle(10, 10, 20, 20).Build());
        Is(At(r, 5, 15), Red);
        Clear(At(r, 15, 15));
    }

    [Fact]
    public void OffsetViewportOriginShiftsTheDrawingTheOtherWay()
    {
        var r = Render(Fill(Red).OffsetViewportOrg(10, 0).Rectangle(0, 10, 10, 20).Build());
        Is(At(r, 15, 15), Red);
        Clear(At(r, 5, 15));
    }

    [Fact]
    public void ScalingTheWindowExtentScalesTheDrawing()
    {
        // The window shrinks to half, so one logical unit becomes two pixels.
        var r = Render(Fill(Red).ScaleWindowExt(1, 2, 1, 2).Rectangle(0, 0, 10, 10).Build());
        Is(At(r, 15, 15), Red);
        Clear(At(r, 25, 15));
    }

    [Fact]
    public void WindowAndViewportMapTheLogicalSpace()
    {
        // The file maps a 10 x 10 window onto the whole picture.
        var r = Render(Fill(Red).SetMapMode(8).SetWindowExt(10, 10).SetViewportExt(100, 100).Rectangle(2, 2, 5, 5).Build());
        Is(At(r, 30, 30), Red);
        Clear(At(r, 10, 10));
        Clear(At(r, 55, 30));
    }

    [Fact]
    public void WindowedPictureScalesWithTheOutputSizeWithoutTheFileKnowing()
    {
        var r = Render(Fill(Red).Rectangle(0, 0, 50, 100).Build(), width: 200);
        Assert.Equal(200, r.W);
        Is(At(r, 50, 100), Red);
        Clear(At(r, 150, 100));
    }

    // ---- drawing -------------------------------------------------------------------------------------------------------

    [Fact]
    public void PolygonFillsAndPolylineStrokes()
    {
        byte[] wmf = Fill(Red).Poly(0x0324, 10, 10, 50, 10, 10, 50).CreatePen(0, 1, Blue).Select(2).Poly(0x0325, 60, 20, 90, 20).Build();
        var r = Render(wmf);
        Is(At(r, 15, 15), Red);
        Clear(At(r, 45, 45));
        Is(At(r, 75, 20), Blue);
        Clear(At(r, 75, 22));
    }

    [Fact]
    public void PolyPolygonFollowsTheFillMode()
    {
        int[] outer = [10, 10, 60, 10, 60, 60, 10, 60], inner = [25, 25, 45, 25, 45, 45, 25, 45];
        int[] all = [.. outer, .. inner];
        var alternate = Render(Fill(Red).SetPolyFillMode(1).PolyPolygon([4, 4], all).Build());
        Is(At(alternate, 15, 15), Red);
        Clear(At(alternate, 35, 35));
        var winding = Render(Fill(Red).SetPolyFillMode(2).PolyPolygon([4, 4], all).Build());
        Is(At(winding, 35, 35), Red);
    }

    [Fact]
    public void EllipseRoundRectAndPieFill()
    {
        byte[] wmf = Fill(Red).Ellipse(0, 0, 40, 40).RoundRect(50, 0, 90, 40, 20, 20).Arc(0x081A, 0, 50, 50, 100, 50, 75, 25, 50).Build();
        var r = Render(wmf);
        Is(At(r, 20, 20), Red);
        Clear(At(r, 1, 1));
        Is(At(r, 70, 20), Red);
        Clear(At(r, 51, 1));
        // The pie is the top-right quarter of the circle at (25, 75).
        Is(At(r, 35, 65), Red);
        Clear(At(r, 15, 85));
    }

    [Fact]
    public void ChordIsFilledAndArcIsOnlyStroked()
    {
        MetafileDocument chord = Parse(Fill(Red).Arc(0x0830, 0, 0, 50, 50, 50, 25, 0, 25).Build());
        Assert.Single(chord.Commands.OfType<FillPathCommand>());
        byte[] arc = Picture().CreatePen(0, 1, Blue).Select(1).Arc(0x0817, 0, 50, 50, 100, 50, 75, 25, 50).Build();
        MetafileDocument doc = Parse(arc);
        Assert.Single(doc.Commands.OfType<StrokePathCommand>());
        Assert.Empty(doc.Commands.OfType<FillPathCommand>());
    }

    [Fact]
    public void SetPixelMoveToAndLineToPaintOnePixelWide()
    {
        byte[] wmf = Picture().CreatePen(0, 1, Blue).Select(1).SetPixel(5, 5, Green).MoveTo(10, 20).LineTo(50, 20).Build();
        var r = Render(wmf);
        Is(At(r, 5, 5), Green);
        Is(At(r, 30, 20), Blue);
        Clear(At(r, 30, 21));
    }

    [Fact]
    public void WidePenIsGeometricAndFillIsUnderIt()
    {
        byte[] wmf = new WmfBuilder().Placeable(0, 0, 100, 100).CreatePen(0, 10, Blue).CreateBrush(0, Red).Select(0).Select(1).Rectangle(20, 20, 80, 80).Build();
        var r = Render(wmf);
        Is(At(r, 50, 50), Red);
        Is(At(r, 20, 50), Blue);
        Is(At(r, 17, 50), Blue);
        Clear(At(r, 10, 50));
    }

    // ---- text ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TextOutHasItsFontColorPositionAndString()
    {
        byte[] wmf = Picture().CreateFont(-20, "Arial", 700, underline: true).Select(1).SetTextColor(Green).SetBkMode(1).TextOut(10, 70, "Hi").Build();
        MetafileDocument doc = Parse(wmf);
        DrawTextCommand t = doc.Commands.OfType<DrawTextCommand>().Single();
        Assert.Equal("Hi", t.Text);
        Assert.Equal("Arial", t.Font.FaceName);
        Assert.Equal(700, t.Font.Weight);
        Assert.True(t.Font.Underline);
        Assert.Equal(new System.Numerics.Vector2(10, 70), t.Origin);
        Assert.Equal(new Rgba32(0, 255, 0, 255), t.Color);
        Assert.Null(t.Background);
    }

    [Fact]
    public void OddLengthStringsAreReadWithTheirPadding()
    {
        MetafileDocument doc = Parse(Picture().TextOut(5, 6, "abc").TextOut(7, 8, "de").Build());
        DrawTextCommand[] t = [.. doc.Commands.OfType<DrawTextCommand>()];
        Assert.Equal(["abc", "de"], t.Select(x => x.Text));
        Assert.Equal(new System.Numerics.Vector2(7, 8), t[1].Origin);
    }

    [Fact]
    public void ExtTextOutCarriesRectangleOptionsAndAdvances()
    {
        byte[] wmf = Picture().SetBkColor(Blue).ExtTextOut(10, 30, 2 | 4, (5, 10, 60, 40), "ABC", [12, 13, 14]).Build();
        DrawTextCommand t = Parse(wmf).Commands.OfType<DrawTextCommand>().Single();
        Assert.Equal("ABC", t.Text);
        Assert.Equal(new RectD(5, 10, 60, 40), t.OpaqueRectangle);
        Assert.Equal(new RectD(5, 10, 60, 40), t.ClipRectangle);
        Assert.Equal(new Rgba32(0, 0, 255, 255), t.OpaqueColor);
        Assert.Equal([12.0, 13.0, 14.0], t.Advances);
    }

    [Fact]
    public void ExtTextOutWithoutARectangleOrAdvances()
    {
        DrawTextCommand t = Parse(Picture().ExtTextOut(10, 30, 0, null, "AB").Build()).Commands.OfType<DrawTextCommand>().Single();
        Assert.Null(t.OpaqueRectangle);
        Assert.Null(t.ClipRectangle);
        Assert.Null(t.Advances);
        Assert.Equal(new System.Numerics.Vector2(10, 30), t.Origin);
    }

    [Fact]
    public void ExtraCharacterSpacingAndAlignmentReachTheCommand()
    {
        DrawTextCommand t = Parse(Picture().SetTextCharExtra(3).SetTextAlign(6 | 24).TextOut(50, 50, "x").Build()).Commands.OfType<DrawTextCommand>().Single();
        Assert.Equal(3, t.CharacterExtra);
        Assert.Equal(TextHorizontalAlignment.Center, t.Horizontal);
        Assert.Equal(TextVerticalAlignment.Baseline, t.Vertical);
    }

    [Fact]
    public void TextIsDrawnWithTheResolvedFont()
    {
        byte[] wmf = Picture().CreateFont(-30, "Arial").Select(1).SetBkMode(1).TextOut(10, 10, "HH").Build();
        var r = Render(wmf, fonts: MetafileRenderTests.TestFonts());
        int ink = r.P.Count(p => p.A > 100);
        Assert.InRange(ink, 100, 2000);
        Assert.Equal(0, r.P.Take(r.W * 9).Count(p => p.A > 0));
    }

    // ---- bitmaps -----------------------------------------------------------------------------------------------------

    private static byte[] Quad() => WmfBuilder.Dib24(2, 2, (x, y) => (x, y) switch { (0, 0) => (255, 0, 0), (1, 0) => (0, 255, 0), (0, 1) => (0, 0, 255), _ => (255, 255, 255) });

    [Fact]
    public void StretchDibScalesTheBitmapAndKeepsItsOrientation()
    {
        var r = Render(Picture().StretchDib(10, 10, 40, 40, 0, 0, 2, 2, 0x00CC0020, Quad()).Build());
        Is(At(r, 15, 15), Red);
        Is(At(r, 45, 15), Green);
        Is(At(r, 15, 45), Blue);
        Is(At(r, 45, 45), WmfBuilder.Rgb(255, 255, 255));
        Clear(At(r, 55, 15));
    }

    [Fact]
    public void StretchDibWithNegativeHeightFlips()
    {
        var r = Render(Picture().StretchDib(10, 50, 40, -40, 0, 0, 2, 2, 0x00CC0020, Quad()).Build());
        Is(At(r, 15, 15), Blue);
        Is(At(r, 15, 45), Red);
    }

    [Fact]
    public void StretchDibTakesASourceRectangle()
    {
        var r = Render(Picture().StretchDib(0, 0, 20, 20, 1, 0, 1, 1, 0x00CC0020, Quad()).Build());
        Is(At(r, 10, 10), Green);
    }

    [Fact]
    public void DibBitBltAndDibStretchBltDrawTheirBitmaps()
    {
        var a = Render(Picture().DibBitBlt(20, 20, 2, 2, 0, 0, 0x00CC0020, Quad()).Build());
        Is(At(a, 20, 20), Red);
        Is(At(a, 21, 20), Green);
        var b = Render(Picture().DibStretchBlt(0, 0, 60, 60, 0, 0, 2, 2, 0x00CC0020, Quad()).Build());
        Is(At(b, 10, 10), Red);
        Is(At(b, 40, 10), Green);
    }

    [Fact]
    public void SetDibToDeviceDrawsPixelsOneToOne()
    {
        var r = Render(Picture().SetDibToDevice(30, 30, 2, 2, 0, 0, Quad(), 2).Build());
        Is(At(r, 30, 30), Red);
        Is(At(r, 31, 30), Green);
        Is(At(r, 30, 31), Blue);
    }

    [Fact]
    public void BlockTransferWithoutABitmapIsAPatternFill()
    {
        // PATCOPY from the current brush; the form without a source bitmap has the reserved word between XSrc and Height.
        var r = Render(Fill(Red).DibBitBltNoBitmap(10, 10, 30, 30, 0x00F00021).Build());
        Is(At(r, 20, 20), Red);
        Clear(At(r, 45, 20));
        var p = Render(Fill(Blue).PatBlt(5, 5, 10, 10, 0x00F00021).Build());
        Is(At(p, 8, 8), Blue);
    }

    [Fact]
    public void DeviceDependentMonochromeBitmapIsBlackAndWhite()
    {
        // 16 x 2 pixels, one bit each: the first row is set (white), the second clear (black).
        byte[] bmp = WmfBuilder.Bitmap16(16, 2, 1, [0xFF, 0xFF, 0x00, 0x00]);
        var r = Render(Picture().BitBlt(10, 10, 16, 2, 0, 0, 0x00CC0020, bmp).Build());
        Is(At(r, 12, 10), WmfBuilder.Rgb(255, 255, 255));
        Is(At(r, 12, 11), 0);
    }

    [Fact]
    public void DibPatternBrushFillsWithTheBitmap()
    {
        byte[] dib = WmfBuilder.Dib24(2, 1, (x, _) => x == 0 ? (255, 0, 0) : (0, 0, 255));
        byte[] wmf = Picture().DibCreatePatternBrush(5, 0, dib).Select(0).Select(1).Rectangle(10, 10, 30, 30).Build();
        var r = Render(wmf);
        Is(At(r, 10, 10), Red);
        Is(At(r, 11, 10), Blue);
        Is(At(r, 12, 10), Red);
    }

    [Fact]
    public void PatternBrushFromAMonochromeBitmapUsesTextAndBackgroundColors()
    {
        // 8 x 8 bits, alternating columns set.
        byte[] rows = new byte[16];
        for (int y = 0; y < 8; y++)
        {
            rows[y * 2] = 0xAA;
        }

        byte[] wmf = Picture().CreatePatternBrush(8, 8, rows).SetTextColor(Red).SetBkColor(Blue).Select(0).Select(1).Rectangle(0, 0, 40, 40).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.Contains(doc.Commands.OfType<FillPathCommand>(), f => f.Brush is PatternBrushSpec);
        var r = Render(wmf);
        Assert.NotEqual(At(r, 0, 4), At(r, 1, 4));
    }

    [Fact]
    public void BitmapUsingThePaletteIndicesIsSkippedWithADiagnostic()
    {
        MetafileDocument doc = Parse(Picture().StretchDib(0, 0, 10, 10, 0, 0, 2, 2, 0x00CC0020, Quad(), usage: 1).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("DIB_PAL_COLORS"));
        Assert.Empty(doc.Commands.OfType<BlitCommand>());
    }

    // ---- regions and clipping --------------------------------------------------------------------------------------------

    [Fact]
    public void RegionFillPaintAndInvert()
    {
        byte[] wmf = Picture().CreateBrush(0, Red).CreateRegion((10, 10, 40, 20), (10, 20, 20, 40)).Select(0).FillRegion(2, 1).PaintRegion(2).Build();
        var r = Render(wmf);
        Is(At(r, 15, 15), Red);
        Is(At(r, 15, 30), Red);
        Clear(At(r, 30, 30));
        var inv = Render(Picture().CreateRegion((0, 0, 20, 20)).InvertRegion(1).Build());
        Is(At(inv, 10, 10), WmfBuilder.Rgb(0, 0, 0));
        Clear(At(inv, 30, 10));
    }

    [Fact]
    public void FrameRegionPaintsInsideTheRegion()
    {
        byte[] wmf = Picture().CreateBrush(0, Red).CreateRegion((10, 10, 50, 50)).FrameRegion(2, 1, 4, 4).Build();
        var r = Render(wmf);
        Is(At(r, 11, 30), Red);
        Is(At(r, 30, 12), Red);
        Clear(At(r, 30, 30));
        Clear(At(r, 8, 30));
    }

    [Fact]
    public void SelectedRegionClipsLaterDrawing()
    {
        byte[] wmf = Fill(Red).CreateRegion((0, 0, 30, 30)).SelectClipRegion(2).Rectangle(0, 0, 100, 100).Build();
        var r = Render(wmf);
        Is(At(r, 10, 10), Red);
        Clear(At(r, 50, 50));
        // A null region (an index that names no region) removes the clip.
        byte[] reset = Fill(Red).CreateRegion((0, 0, 30, 30)).SelectClipRegion(2).SelectClipRegion(77).Rectangle(0, 0, 100, 100).Build();
        Is(At(Render(reset), 50, 50), Red);
    }

    [Fact]
    public void SelectingARegionObjectSetsTheClip()
    {
        byte[] wmf = Fill(Red).CreateRegion((0, 0, 30, 30)).Select(2).Rectangle(0, 0, 100, 100).Build();
        var r = Render(wmf);
        Is(At(r, 10, 10), Red);
        Clear(At(r, 50, 50));
    }

    [Fact]
    public void ClipRectanglesIntersectExcludeAndOffset()
    {
        var r = Render(Fill(Red).IntersectClipRect(10, 10, 60, 60).ExcludeClipRect(30, 30, 40, 40).Rectangle(0, 0, 100, 100).Build());
        Is(At(r, 15, 15), Red);
        Clear(At(r, 35, 35));
        Clear(At(r, 70, 70));
        var moved = Render(Fill(Red).CreateRegion((0, 0, 20, 20)).SelectClipRegion(2).OffsetClipRgn(30, 0).Rectangle(0, 0, 100, 100).Build());
        Is(At(moved, 40, 10), Red);
        Clear(At(moved, 10, 10));
    }

    // ---- ignored and unknown records ----------------------------------------------------------------------------------------

    [Fact]
    public void PalettePrintingAndEscapeRecordsAreReportedAsHavingNoEffect()
    {
        byte[] wmf = Picture().CreatePalette().Rec(0x0234, 1).Rec(0x0035).Rec(0x014D).Escape(0x0013, [1, 2]).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.All(doc.Diagnostics, d => Assert.NotEqual(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Palette"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("escape 0x0013"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("0x014D"));
    }

    [Fact]
    public void UnknownFunctionsAndFloodFillsAreSkippedWithADiagnostic()
    {
        MetafileDocument doc = Parse(Picture().Rec(0x1234, 1, 2).Rec(0x0419, 0, 0, 0, 5, 5).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("0x1234"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Flood"));
    }

    [Fact]
    public void DiagnosticsNameTheWmfRecordAndFunction()
    {
        MetafileDocument doc = Parse(Picture().Rec(0x1234, 1).Select(9).Build());
        DiagnosticCommand d = Assert.Single(doc.Diagnostics, x => x.Message.Contains("object 9"));
        Assert.Equal(2, d.RecordIndex);
        Assert.Equal(0x012D, d.RecordType);
    }

    [Fact]
    public void TruncatedFileKeepsWhatWasDrawnAndSaysSo()
    {
        byte[] wmf = Fill(Red).Rectangle(0, 0, 10, 10).Rectangle(20, 20, 30, 30).Build(eof: false);
        MetafileDocument doc = Parse(wmf.AsMemory(0, wmf.Length - 5).ToArray());
        Assert.Single(doc.Commands.OfType<FillPathCommand>());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("truncated"));
    }

    [Fact]
    public void RecordWithAnImpossibleSizeEndsTheFile()
    {
        byte[] wmf = Fill(Red).Rectangle(0, 0, 10, 10).Build(eof: false);
        byte[] bad = [.. wmf, 1, 0, 0, 0, 0x1B, 0x04];
        MetafileDocument doc = Parse(bad);
        Assert.Single(doc.Commands.OfType<FillPathCommand>());
        Assert.Contains(doc.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ShortRecordsAreSkippedNotRead()
    {
        // A rectangle record that stops after its first parameter, then a good one.
        byte[] wmf = Fill(Red).Rec(0x041B, 10).Rectangle(0, 0, 10, 10).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.Single(doc.Commands.OfType<FillPathCommand>());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("shorter"));
    }

    // ---- embedded enhanced metafile -----------------------------------------------------------------------------------------

    private static byte[] EmbeddedRedEmf() => new EmfBuilder(100).SelectStock(8).CreateBrush(1, 0, EmfBuilder.Rgb(255, 0, 0)).Select(1).Rectangle(0, 0, 50, 50).Build();

    private static WmfBuilder WithGreenFallback() => new WmfBuilder().Placeable(0, 0, 100, 100).CreatePen(5, 0, 0).CreateBrush(0, Green).Select(0).Select(1);

    [Fact]
    public void EmbeddedEnhancedMetafileIsReassembledAndPlayed()
    {
        byte[] emf = EmbeddedRedEmf();
        byte[] wmf = WithGreenFallback().EmbedEmf(emf, chunk: 100).Rectangle(0, 0, 50, 50).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.Equal(MetafileKind.Wmf, doc.Info.Kind);
        Assert.Equal(new Rgba32(255, 0, 0, 255), Assert.IsType<SolidBrushSpec>(doc.Commands.OfType<FillPathCommand>().Single().Brush).Color);
        Is(At(Render(wmf), 20, 20), Red);
    }

    [Fact]
    public void EmbeddedEmfCanBeTurnedOffToDrawTheWmfRecords()
    {
        byte[] wmf = WithGreenFallback().EmbedEmf(EmbeddedRedEmf()).Rectangle(0, 0, 50, 50).Build();
        MetafileDocument doc = Parse(wmf, new MetafileReadOptions { PreferEmbeddedEmf = false });
        Assert.Equal(new Rgba32(0, 255, 0, 255), Assert.IsType<SolidBrushSpec>(doc.Commands.OfType<FillPathCommand>().Single().Brush).Color);
    }

    [Fact]
    public void InconsistentChunksFallBackToTheWmfRecords()
    {
        byte[] wmf = WithGreenFallback().EmbedEmf(EmbeddedRedEmf(), chunk: 100, corruptRemainingAt: 1).Rectangle(0, 0, 50, 50).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.Equal(new Rgba32(0, 255, 0, 255), Assert.IsType<SolidBrushSpec>(doc.Commands.OfType<FillPathCommand>().Single().Brush).Color);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("inconsistent"));
    }

    [Fact]
    public void MissingChunksFallBackToTheWmfRecords()
    {
        byte[] wmf = WithGreenFallback().EmbedEmf(EmbeddedRedEmf(), chunk: 100).DropLast().Rectangle(0, 0, 50, 50).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.Equal(new Rgba32(0, 255, 0, 255), Assert.IsType<SolidBrushSpec>(doc.Commands.OfType<FillPathCommand>().Single().Brush).Color);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("inconsistent") || d.Message.Contains("incomplete"));
    }

    [Fact]
    public void EmbeddedFileThatIsNotAnEmfFallsBack()
    {
        byte[] junk = new byte[200];
        byte[] wmf = WithGreenFallback().EmbedEmf(junk).Rectangle(0, 0, 50, 50).Build();
        MetafileDocument doc = Parse(wmf);
        Assert.Equal(new Rgba32(0, 255, 0, 255), Assert.IsType<SolidBrushSpec>(doc.Commands.OfType<FillPathCommand>().Single().Brush).Color);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("embedded"));
    }

    // ---- limits and hostile files ----------------------------------------------------------------------------------------------

    [Fact]
    public void RecordCountLimitIsEnforced()
    {
        var b = Picture();
        for (int i = 0; i < 50; i++)
        {
            b.SetPixel(i, 0, Red);
        }

        Assert.Throws<ImageLimitException>(() => Parse(b.Build(), Limited(new MetafileLimits { MaxRecords = 20 })));
        Assert.Throws<ImageLimitException>(() => MetafileDocument.Identify(b.Build(), Limited(new MetafileLimits { MaxRecords = 20 })));
    }

    [Fact]
    public void FileSizeLimitIsEnforced()
    {
        Assert.Throws<ImageLimitException>(() => Parse(Fill(Red).Rectangle(0, 0, 5, 5).Build(), Limited(new MetafileLimits { MaxFileBytes = 40 })));
    }

    [Fact]
    public void EmbeddedEmfSizeIsCheckedBeforeAnythingIsAllocated()
    {
        // The first chunk declares a 1 GiB enhanced metafile; the file holds nothing like it.
        var body = new byte[34 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x43464D57);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(26), (1u << 30) - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(30), 1u << 30);
        byte[] wmf = Picture().Escape(0x000F, body).Build();
        Assert.Throws<ImageLimitException>(() => Parse(wmf));
        // A smaller limit applies to honest files too.
        byte[] honest = WithGreenFallback().EmbedEmf(EmbeddedRedEmf()).Build();
        Assert.Throws<ImageLimitException>(() => Parse(honest, Limited(new MetafileLimits { MaxEmbeddedEmfBytes = 100 })));
    }

    [Fact]
    public void RegionRectangleLimitIsEnforced()
    {
        byte[] wmf = Picture().CreateRegion((0, 0, 5, 5), (0, 5, 5, 10), (0, 10, 5, 15)).Build();
        Assert.Throws<ImageLimitException>(() => Parse(wmf, Limited(new MetafileLimits { MaxRegionRectangles = 2 })));
    }

    [Fact]
    public void RegionThatDeclaresMoreScansThanItHoldsIsTreatedAsEmpty()
    {
        byte[] region = WmfBuilder.Region((0, 0, 5, 5));
        BinaryPrimitives.WriteInt16LittleEndian(region.AsSpan(10), 30000); // ScanCount
        MetafileDocument doc = Parse(Picture().Raw(0x06FF, region).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("region"));
    }

    [Fact]
    public void ObjectTableLimitIsEnforced()
    {
        var b = new WmfBuilder().Placeable(0, 0, 10, 10);
        for (int i = 0; i < 6; i++)
        {
            b.CreateBrush(0, Red);
        }

        MetafileDocument doc = Parse(b.Build(), Limited(new MetafileLimits { MaxObjects = 4 }));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("object table is full"));
    }

    [Fact]
    public void BitmapPixelLimitIsEnforcedBeforeTheBitmapIsBuilt()
    {
        byte[] bmp = WmfBuilder.Bitmap16(64, 64, 1, new byte[8 * 64]);
        byte[] wmf = Picture().BitBlt(0, 0, 64, 64, 0, 0, 0x00CC0020, bmp).Build();
        Assert.Throws<ImageLimitException>(() => Parse(wmf, Limited(new MetafileLimits { MaxBitmapPixels = 100 })));
        // A bitmap that declares more than its record holds is refused without allocating for the declaration.
        byte[] huge = WmfBuilder.Bitmap16(30000, 30000, 1, new byte[16]);
        MetafileDocument doc = Parse(Picture().BitBlt(0, 0, 10, 10, 0, 0, 0x00CC0020, huge).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("larger than its record"));
        // The same for a DIB, whose size the shared bitmap decoder checks.
        byte[] dib = WmfBuilder.Dib24(2, 2, (_, _) => (0, 0, 0));
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 40000);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 40000);
        MetafileDocument bad = Parse(Picture().StretchDib(0, 0, 10, 10, 0, 0, 2, 2, 0x00CC0020, dib).Build());
        Assert.Empty(bad.Commands.OfType<BlitCommand>());
    }

    [Fact]
    public void TextLengthLimitIsEnforced()
    {
        MetafileDocument doc = Parse(Picture().TextOut(1, 1, "a long enough string").Build(), Limited(new MetafileLimits { MaxTextCharacters = 5 }));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("does not fit"));
        Assert.Empty(doc.Commands.OfType<DrawTextCommand>());
    }

    [Fact]
    public void PointCountsThatDoNotFitTheRecordAreSkipped()
    {
        // Polygon claiming 30,000 points in a 3-point record, and a PolyPolygon claiming polygons and points it does not have.
        byte[] poly = new WmfBuilder().Rec(0x0324, 30000, 1, 2, 3, 4).Build();
        byte[] polyPoly = new WmfBuilder().Rec(0x0538, 2, 30000, 30000, 1, 2).Build();
        Assert.Contains(Parse(poly).Diagnostics, d => d.Message.Contains("do not fit"));
        Assert.Contains(Parse(polyPoly).Diagnostics, d => d.Message.Contains("PolyPolygon"));
    }

    [Fact]
    public void RandomBytesBehindAValidHeaderNeverEscapeAsUntypedErrors()
    {
        var rng = new Random(12345);
        byte[] head = Fill(Red).Rectangle(0, 0, 10, 10).Build(eof: false);
        for (int round = 0; round < 300; round++)
        {
            byte[] junk = new byte[rng.Next(0, 400)];
            rng.NextBytes(junk);
            try
            {
                _ = Parse([.. head, .. junk], Limited(new MetafileLimits { MaxWork = 1 << 24 }));
            }
            catch (ImageException)
            {
            }
        }
    }

    // ---- through Image.Load ---------------------------------------------------------------------------------------------------

    [Fact]
    public void LoadResizeAndSaveWork()
    {
        byte[] wmf = Fill(Red).Rectangle(0, 0, 50, 100).Build();
        var config = new ImageConfiguration().Add(WmfFormat.Instance);
        using var ms = new MemoryStream();
        Image.Load(wmf, new LoadOptions { Configuration = config }).Resize(width: 40).Save(ms, PngFormat.Instance);
        ms.Position = 0;
        Image back = Image.Load(ms);
        Assert.Equal(40, back.Width);
        Rgba32[] px = back.ToArray<Rgba32>();
        Assert.Equal(255, px[(20 * 40) + 5].R);
        Assert.Equal(0, px[(20 * 40) + 35].A);
    }

    [Fact]
    public void PlaceableAndPlainFilesDrawTheSame()
    {
        byte[] a = new WmfBuilder().Placeable(0, 0, 100, 100).CreatePen(5, 0, 0).CreateBrush(0, Red).Select(0).Select(1).Rectangle(10, 10, 60, 60).Build();
        byte[] b = new WmfBuilder().SetMapMode(8).SetWindowOrg(0, 0).SetWindowExt(100, 100).CreatePen(5, 0, 0).CreateBrush(0, Red).Select(0).Select(1).Rectangle(10, 10, 60, 60).Build();
        Assert.Equal(Render(a).P, Render(b).P);
    }
}
