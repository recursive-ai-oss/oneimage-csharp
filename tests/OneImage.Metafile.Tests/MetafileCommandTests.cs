using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Metafile.Tests;

public sealed class MetafileCommandTests
{
    private sealed class Recorder : DrawingCommandVisitor
    {
        public List<string> Names { get; } = [];

        public List<DrawingCommand> All { get; } = [];

        public override void VisitBeginPicture(BeginPictureCommand c) => Add(c);

        public override void VisitSaveState(SaveStateCommand c) => Add(c);

        public override void VisitRestoreState(RestoreStateCommand c) => Add(c);

        public override void VisitSetTransform(SetTransformCommand c) => Add(c);

        public override void VisitClip(ClipCommand c) => Add(c);

        public override void VisitFillPath(FillPathCommand c) => Add(c);

        public override void VisitStrokePath(StrokePathCommand c) => Add(c);

        public override void VisitDrawText(DrawTextCommand c) => Add(c);

        public override void VisitBlit(BlitCommand c) => Add(c);

        public override void VisitDiagnostic(DiagnosticCommand c) => Add(c);

        public override void VisitComment(CommentCommand c) => Add(c);

        private void Add(DrawingCommand c)
        {
            Names.Add(c.Name);
            All.Add(c);
        }
    }

    private static readonly int Red = EmfBuilder.Rgb(255, 0, 0);
    private static readonly int Blue = EmfBuilder.Rgb(0, 0, 255);

    [Fact]
    public void WalkingANonTrivialFileGivesResolvedCommands()
    {
        byte[] emf = new EmfBuilder(100)
            .CreatePen(1, 0, 3, Blue).CreateBrush(2, 0, Red).CreateFont(3, -20, "Arial", 700, underline: true)
            .Select(1).Select(2).Select(3)
            .SaveDc().MapMode(8).WindowExt(10, 10).ViewportExt(100, 100)
            .Rectangle(1, 1, 5, 5)
            .RestoreDc(-1)
            .SetTextColor(EmfBuilder.Rgb(0, 128, 0)).SetBkMode(1).ExtTextOutW("Hi", 10, 70)
            .IntersectClipRect(0, 0, 50, 50).Ellipse(0, 0, 40, 40)
            .Build();
        MetafileDocument doc = MetafileDocument.Parse(emf);
        var rec = new Recorder();
        doc.Play(rec);

        Assert.Equal("begin-picture", rec.Names[0]);
        Assert.Equal(MetafileKind.Emf, doc.Info.Kind);
        Assert.Equal(100, doc.Info.PictureRectangle.Width, 6);
        Assert.Contains("save", rec.Names);

        // The rectangle under the anisotropic mapping carries the mapping as one transform and its pen and brush as plain values.
        int save = rec.Names.IndexOf("save");
        SetTransformCommand xf = Assert.IsType<SetTransformCommand>(rec.All[save + 1]);
        Assert.Equal(10f, xf.Transform.M11, 4);
        Assert.Equal(10f, xf.Transform.M22, 4);
        FillPathCommand fill = Assert.IsType<FillPathCommand>(rec.All[save + 2]);
        Assert.Equal(new SolidBrushSpec(new Rgba32(255, 0, 0, 255)), fill.Brush);
        StrokePathCommand stroke = Assert.IsType<StrokePathCommand>(rec.All[save + 3]);
        Assert.Equal(3, stroke.Pen.Width);
        Assert.False(stroke.Pen.IsCosmetic);
        Assert.Equal(new Rgba32(0, 0, 255, 255), stroke.Pen.Color);
        Assert.IsType<RestoreStateCommand>(rec.All[save + 4]);

        // After the restore the transform is the identity again, and the text arrives with its font, color and position resolved.
        DrawTextCommand text = rec.All.OfType<DrawTextCommand>().Single();
        Assert.Equal("Hi", text.Text);
        Assert.Equal(new Rgba32(0, 128, 0, 255), text.Color);
        Assert.Equal("Arial", text.Font.FaceName);
        Assert.Equal(700, text.Font.Weight);
        Assert.True(text.Font.Underline);
        Assert.Null(text.Background);
        Assert.Equal(new Vector2(10, 70), text.Origin);
        int textIndex = rec.All.IndexOf(text);
        // The restore brings the identity back by itself; no further set-transform is needed between it and the text.
        Assert.DoesNotContain(rec.All.Skip(save + 5).Take(textIndex - save - 5).OfType<SetTransformCommand>(), t => !t.Transform.IsIdentity);

        ClipCommand clip = rec.All.OfType<ClipCommand>().Single();
        Assert.Equal(ClipMode.Intersect, clip.Mode);
        Assert.Equal(4, clip.Path!.Points.Count);

        // Names and parameters are language-neutral strings.
        Assert.All(rec.All, c => Assert.False(string.IsNullOrEmpty(c.Name)));
        Assert.Equal("solid #ff0000ff", fill.Describe()["brush"]);
    }

    [Fact]
    public void RestoresNeverExceedSaves()
    {
        byte[] emf = new EmfBuilder().SaveDc().SaveDc().SaveDc().RestoreDc(-1).RestoreDc(-5).Rectangle(1, 1, 3, 3).Build();
        var rec = new Recorder();
        MetafileDocument.Parse(emf).Play(rec);
        int depth = 0;
        foreach (DrawingCommand c in rec.All)
        {
            depth += c is SaveStateCommand ? 1 : c is RestoreStateCommand ? -1 : 0;
            Assert.True(depth >= 0);
        }

        Assert.Equal(0, depth);
        Assert.Contains(rec.All, c => c is DiagnosticCommand d && d.Message.Contains("RestoreDC"));
    }

    [Fact]
    public void UnknownRecordsAndMissingObjectsBecomeDiagnostics()
    {
        byte[] emf = new EmfBuilder().Ints(9999, 1, 2).Select(77).Ints(40, 5).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Unknown record type 9999"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("object 77"));
    }

    [Fact]
    public void EmfPlusCommentsAreDetectedAndReported()
    {
        // The first record after the header is an EMF+ header record inside a comment.
        byte[] plus = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(plus, 0x4001);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(12), 0xDBC01002);
        byte[] emf = new EmfBuilder().Comment("EMF+", plus).Rectangle(1, 1, 5, 5).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf);
        Assert.True(doc.Info.HasEmfPlus);
        Assert.Contains(doc.Commands, c => c is CommentCommand { Kind: CommentKind.EmfPlus });
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("EMF+"));
    }

    private static byte[] EmfPlusHeader(ushort recordFlags, uint emfPlusFlags)
    {
        // [MS-EMFPLUS] 2.3.3.3 EmfPlusHeader: Type, Flags (bit 0 = dual), Size, DataSize, Version, EmfPlusFlags (bit 0 = video display), DPI x and y.
        var plus = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(plus, 0x4001);
        BinaryPrimitives.WriteUInt16LittleEndian(plus.AsSpan(2), recordFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(12), 0xDBC01002);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(16), emfPlusFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(20), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(plus.AsSpan(24), 96);
        return plus;
    }

    [Theory]
    [InlineData(1, 0, true)]   // dual, recorded for a printer
    [InlineData(1, 1, true)]   // dual, recorded for a video display
    [InlineData(0, 1, false)]  // EMF+ only, recorded for a video display: the display bit must not make it dual
    [InlineData(0, 0, false)]
    public void DualIsTheHeaderRecordFlagNotTheVideoDisplayFlag(int recordFlags, int emfPlusFlags, bool dual)
    {
        byte[] emf = new EmfBuilder().Comment("EMF+", EmfPlusHeader((ushort)recordFlags, (uint)emfPlusFlags)).Rectangle(1, 1, 5, 5).Build();
        MetafileInfo info = MetafileDocument.Identify(emf);
        Assert.True(info.HasEmfPlus);
        Assert.Equal(!dual, info.IsEmfPlusOnly);
    }

    [Fact]
    public void NotAnEmfIsAFormatError()
    {
        Assert.Throws<ImageFormatException>(() => MetafileDocument.Parse(new byte[200]));
        Assert.Throws<ImageFormatException>(() => MetafileDocument.Parse(new byte[10]));
        Assert.False(MetafileDocument.IsEmf(new byte[100]));
    }

    [Fact]
    public void TruncatedFileKeepsWhatWasDrawnAndSaysSo()
    {
        byte[] emf = new EmfBuilder().SelectStock(8).CreateBrush(1, 0, Red).Select(1).Rectangle(1, 1, 50, 50).Rectangle(2, 2, 60, 60).Build(eof: false);
        byte[] cut = emf[..^6];
        MetafileDocument doc = MetafileDocument.Parse(cut);
        Assert.Single(doc.Commands.OfType<FillPathCommand>());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("truncated"));
    }

    [Fact]
    public void RecordWithImpossibleSizeStopsInterpretation()
    {
        byte[] emf = new EmfBuilder().Rectangle(1, 1, 5, 5).Build(eof: false);
        byte[] bad = [.. emf, 43, 0, 0, 0, 3, 0, 0, 0];
        MetafileDocument doc = MetafileDocument.Parse(bad);
        Assert.Contains(doc.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // ---- limits ----------------------------------------------------------------------------------------------------

    private static MetafileReadOptions With(MetafileLimits l) => new() { Limits = l };

    [Fact]
    public void RecordCountLimitThrows()
    {
        var b = new EmfBuilder();
        for (int i = 0; i < 50; i++)
        {
            b.Rectangle(1, 1, 2, 2);
        }

        Assert.Throws<ImageLimitException>(() => MetafileDocument.Parse(b.Build(), With(new MetafileLimits { MaxRecords = 20 })));
    }

    [Fact]
    public void CommandCountLimitThrows()
    {
        var b = new EmfBuilder();
        for (int i = 0; i < 50; i++)
        {
            b.Rectangle(1, 1, 2, 2);
        }

        Assert.Throws<ImageLimitException>(() => MetafileDocument.Parse(b.Build(), With(new MetafileLimits { MaxCommands = 30 })));
    }

    [Fact]
    public void FileSizeLimitThrows() =>
        Assert.Throws<ImageLimitException>(() => MetafileDocument.Parse(new EmfBuilder().Rectangle(1, 1, 2, 2).Build(), With(new MetafileLimits { MaxFileBytes = 100 })));

    [Fact]
    public void WorkLimitThrows()
    {
        var b = new EmfBuilder();
        for (int i = 0; i < 100; i++)
        {
            b.Rectangle(1, 1, 2, 2);
        }

        Assert.Throws<ImageLimitException>(() => MetafileDocument.Parse(b.Build(), With(new MetafileLimits { MaxWork = 50 })));
    }

    [Fact]
    public void SaveDepthLimitThrows()
    {
        var b = new EmfBuilder();
        for (int i = 0; i < 20; i++)
        {
            b.SaveDc();
        }

        Assert.Throws<ImageLimitException>(() => MetafileDocument.Parse(b.Build(), With(new MetafileLimits { MaxSaveDepth = 10 })));
    }

    [Fact]
    public void ObjectIndexBeyondTheTableIsRefusedWithoutAllocating()
    {
        MetafileDocument doc = MetafileDocument.Parse(new EmfBuilder().CreateBrush(int.MaxValue, 0, Red).CreateBrush(70_000, 0, Red).Build());
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Message.Contains("outside 1..")));
    }

    [Fact]
    public void PointCountsThatDoNotFitTheRecordAreSkipped()
    {
        // A polygon record claiming 2^30 points in 28 bytes.
        var body = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 1 << 30);
        byte[] emf = new EmfBuilder().Raw(3, body).Raw(86, body).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf);
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Message.Contains("do not fit")));
    }

    [Fact]
    public void PointLimitAppliesBeforeAllocation()
    {
        byte[] emf = new EmfBuilder().Polygon(3, false, 0, 0, 1, 0, 1, 1, 0, 1, 5, 5).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf, With(new MetafileLimits { MaxPathPoints = 4 }));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("do not fit"));
        Assert.DoesNotContain(doc.Commands, c => c is FillPathCommand);
    }

    [Fact]
    public void HugeBitmapHeaderIsRefusedBeforeDecoding()
    {
        var b = new EmfBuilder();
        byte[] bmi = EmfBuilder.BitmapInfo(60000, 60000, 24);
        // Bounds(16) xDest yDest xSrc ySrc cxSrc cySrc offBmi cbBmi offBits cbBits ... in a record that carries a small buffer.
        var body = new byte[(15 * 4) + 16 + bmi.Length + 16];
        void W(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at - 8), v);
        W(40, 60000);
        W(44, 60000);
        int fixedSize = 8 + 16 + (15 * 4);
        W(48, fixedSize);
        W(52, bmi.Length);
        W(56, fixedSize + bmi.Length);
        W(60, 16);
        bmi.CopyTo(body, fixedSize - 8);
        b.Raw(81, body);
        MetafileDocument doc = MetafileDocument.Parse(b.Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("exceeds the limit"));
        Assert.DoesNotContain(doc.Commands, c => c is BlitCommand);
    }

    [Fact]
    public void BitmapPixelLimitIsEnforced()
    {
        byte[] emf = new EmfBuilder().StretchDibits(0, 0, 10, 10, 20, 20, (x, y) => (1, 2, 3)).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf, With(new MetafileLimits { MaxBitmapPixels = 100 }));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("exceeds the limit"));
        Assert.Throws<ImageLimitException>(() => MetafileDocument.Parse(emf, With(new MetafileLimits { MaxTotalBitmapPixels = 100 })));
    }

    [Fact]
    public void TextLengthLimitIsEnforced()
    {
        byte[] emf = new EmfBuilder().ExtTextOutW("a long enough string", 1, 1).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf, With(new MetafileLimits { MaxTextCharacters = 5 }));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("does not fit"));
        Assert.DoesNotContain(doc.Commands, c => c is DrawTextCommand);
    }

    [Fact]
    public void RegionRectangleLimitIsEnforced()
    {
        // EMR_EXTSELECTCLIPRGN with a region of 100,000 rectangles declared but not present.
        var body = new byte[8 + 32];
        BinaryPrimitives.WriteInt32LittleEndian(body, 32);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 32);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 100_000);
        MetafileDocument doc = MetafileDocument.Parse(new EmfBuilder().Raw(75, body).Build());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("do not fit"));
        Assert.DoesNotContain(doc.Commands, c => c is ClipCommand);
    }

    [Fact]
    public void UserStylePenWithTooManyDashesIsDrawnSolid()
    {
        var body = new byte[4 + 16 + 24];
        BinaryPrimitives.WriteInt32LittleEndian(body, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), 0x10000 | 7);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(40), 1_000_000);
        byte[] emf = new EmfBuilder().Raw(95, body).Select(1).MoveTo(0, 0).LineTo(9, 9).Build();
        MetafileDocument doc = MetafileDocument.Parse(emf);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("dash entries"));
        Assert.Null(doc.Commands.OfType<StrokePathCommand>().Single().Pen.Dashes);
    }

    [Fact]
    public void RandomDamageNeverEscapesAsAnUntypedException()
    {
        byte[] emf = new EmfBuilder().CreatePen(1, 0, 3, Blue).CreateBrush(2, 0, Red).Select(1).Select(2).Rectangle(1, 1, 50, 50).Ellipse(2, 2, 40, 40)
            .Polygon(3, false, 1, 1, 20, 1, 20, 20).ExtTextOutW("hello", 5, 5).StretchDibits(0, 0, 8, 8, 4, 4, (x, y) => (1, 2, 3)).BeginPath().Rectangle(1, 1, 9, 9).EndPath().FillPath().Build();
        var rng = new Random(12345);
        for (int i = 0; i < 400; i++)
        {
            byte[] copy = (byte[])emf.Clone();
            int hits = 1 + rng.Next(4);
            for (int k = 0; k < hits; k++)
            {
                copy[108 + rng.Next(copy.Length - 108)] = (byte)rng.Next(256);
            }

            try
            {
                MetafileDocument.Parse(copy, With(new MetafileLimits { MaxWork = 1_000_000, MaxBitmapPixels = 1 << 20 }));
            }
            catch (ImageException)
            {
                // typed failures are fine
            }
        }
    }
}
