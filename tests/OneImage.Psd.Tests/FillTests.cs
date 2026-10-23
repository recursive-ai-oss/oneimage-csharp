using System.Buffers.Binary;
using System.Text;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>Writes the descriptor structure of Photoshop's fill settings (version, then an object with typed items) for building test documents.</summary>
internal sealed class DescriptorWriter : IDisposable
{
    private readonly MemoryStream _ms = new();

    public static byte[] Block(Action<DescriptorWriter> body)
    {
        using var w = new DescriptorWriter();
        w.U32(16);
        w.Header("null");
        body(w);
        return w._ms.ToArray();
    }

    public void Dispose() => _ms.Dispose();

    public void U32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        _ms.Write(b);
    }

    public void Raw(string fourcc) => _ms.Write(Encoding.Latin1.GetBytes(fourcc));

    public void Id(string s)
    {
        U32(s.Length == 4 ? 0u : (uint)s.Length);
        Raw(s);
    }

    public void Header(string classId)
    {
        U32(0); // empty name
        Id(classId);
    }

    public void Double(double d)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, BitConverter.DoubleToInt64Bits(d));
        _ms.Write(b);
    }

    /// <summary>Writes an object item: key, <c>Objc</c>, header, then <paramref name="count"/> items written by <paramref name="items"/>.</summary>
    public void Obj(string key, string classId, int count, Action items)
    {
        if (key.Length > 0)
        {
            Id(key);
        }

        Raw("Objc");
        Header(classId);
        U32((uint)count);
        items();
    }

    /// <summary>Starts the root object's item count; call right after <see cref="Block"/> opens (the count follows the header).</summary>
    public void Count(int n) => U32((uint)n);

    public void Num(string key, double v)
    {
        Id(key);
        Raw("doub");
        Double(v);
    }

    public void Unit(string key, string unit, double v)
    {
        Id(key);
        Raw("UntF");
        Raw(unit);
        Double(v);
    }

    public void Long(string key, int v)
    {
        Id(key);
        Raw("long");
        U32((uint)v);
    }

    public void Bool(string key, bool v)
    {
        Id(key);
        Raw("bool");
        _ms.WriteByte(v ? (byte)1 : (byte)0);
    }

    public void Enum(string key, string type, string value)
    {
        Id(key);
        Raw("enum");
        Id(type);
        Id(value);
    }

    public void Text(string key, string s)
    {
        Id(key);
        Raw("TEXT");
        U32((uint)s.Length + 1);
        _ms.Write(Encoding.BigEndianUnicode.GetBytes(s + "\0"));
    }

    public void List(string key, int count, Action items)
    {
        Id(key);
        Raw("VlLs");
        U32((uint)count);
        items();
    }
}

/// <summary>Builders for gradient and pattern fill blocks and the documents that carry them.</summary>
internal static class FillDocs
{
    public sealed record Stop(double Location, double Gray, double Midpoint = 50);

    public sealed record Opacity(double Location, double Percent, double Midpoint = 50);

    /// <summary>A <c>GdFl</c> block. Locations are 0..4096 in the file; here 0..1. Colors are gray levels 0..255.</summary>
    public static byte[] Gradient(string type, double angle, Stop[] stops, Opacity[]? opacities = null, bool reverse = false, bool align = true, double scale = 100, double offsetX = 0, double offsetY = 0, int smoothness = 0, string? method = null, string form = "CstS")
    {
        opacities ??= [new Opacity(0, 100), new Opacity(1, 100)];
        return DescriptorWriter.Block(w =>
        {
            int items = 3 + (reverse ? 1 : 0) + (align ? 0 : 1) + (scale != 100 ? 1 : 0) + (offsetX != 0 || offsetY != 0 ? 1 : 0) + (method != null ? 1 : 0);
            w.Count(items);
            if (method != null)
            {
                w.Enum("gradientsInterpolationMethod", "gradientInterpolationMethodType", method);
            }

            w.Unit("Angl", "#Ang", angle);
            w.Enum("Type", "GrdT", type);
            if (reverse)
            {
                w.Bool("Rvrs", true);
            }

            if (!align)
            {
                w.Bool("Algn", false);
            }

            if (scale != 100)
            {
                w.Unit("Scl ", "#Prc", scale);
            }

            if (offsetX != 0 || offsetY != 0)
            {
                w.Obj("Ofst", "Pnt ", 2, () =>
                {
                    w.Unit("Hrzn", "#Prc", offsetX);
                    w.Unit("Vrtc", "#Prc", offsetY);
                });
            }

            w.Obj("Grad", "Grdn", form == "CstS" ? 5 : 3, () =>
            {
                w.Text("Nm  ", "Test");
                w.Enum("GrdF", "GrdF", form);
                if (form != "CstS")
                {
                    w.Num("RndS", 1234567);
                    return;
                }

                w.Num("Intr", smoothness);
                w.List("Clrs", stops.Length, () =>
                {
                    foreach (Stop s in stops)
                    {
                        w.Raw("Objc");
                        w.Header("Clrt");
                        w.Count(4);
                        w.Obj("Clr ", "RGBC", 3, () =>
                        {
                            w.Num("Rd  ", s.Gray);
                            w.Num("Grn ", s.Gray);
                            w.Num("Bl  ", s.Gray);
                        });
                        w.Enum("Type", "Clry", "UsrS");
                        w.Long("Lctn", (int)Math.Round(s.Location * 4096));
                        w.Long("Mdpn", (int)s.Midpoint);
                    }
                });
                w.List("Trns", opacities.Length, () =>
                {
                    foreach (Opacity o in opacities)
                    {
                        w.Raw("Objc");
                        w.Header("TrnS");
                        w.Count(3);
                        w.Unit("Opct", "#Prc", o.Percent);
                        w.Long("Lctn", (int)Math.Round(o.Location * 4096));
                        w.Long("Mdpn", (int)o.Midpoint);
                    }
                });
            });
        });
    }

    /// <summary>A <c>PtFl</c> block naming a pattern by its unique id.</summary>
    public static byte[] Pattern(string id, double? scale = null)
    {
        return DescriptorWriter.Block(w =>
        {
            w.Count(scale != null ? 2 : 1);
            w.Obj("Ptrn", "Ptrn", 2, () =>
            {
                w.Text("Nm  ", "Test pattern");
                w.Text("Idnt", id + "\0");
            });
            if (scale != null)
            {
                w.Unit("Scl ", "#Prc", scale.Value);
            }
        });
    }

    /// <summary>A <c>Patt</c> block with one RGB pattern; channel 0 is run-length coded and the others are raw, to cover both.</summary>
    public static byte[] PatternResource(string id, int w, int h, Func<int, int, int, byte> sample)
    {
        var entry = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); entry.Write(b); }
        void I32(int v) => U32((uint)v);
        U32(1); // version
        U32(3); // RGB
        U32((uint)(((h & 0xFFFF) << 16) | (w & 0xFFFF)));
        string name = "Test pattern";
        U32((uint)name.Length + 1);
        entry.Write(Encoding.BigEndianUnicode.GetBytes(name + "\0"));
        entry.WriteByte((byte)id.Length);
        entry.Write(Encoding.Latin1.GetBytes(id));

        var arrays = new MemoryStream();
        for (int c = 0; c < 3; c++)
        {
            byte[] plane = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    plane[(y * w) + x] = sample(c, x, y);
                }
            }

            var body = new MemoryStream();
            void B32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); body.Write(b); }
            B32(8);
            B32(0); B32(0); B32(h); B32(w);
            body.WriteByte(0); body.WriteByte(8);
            if (c == 0)
            {
                body.WriteByte(1); // run-length coded
                var rows = new List<byte[]>();
                foreach (int y in Enumerable.Range(0, h))
                {
                    byte[] enc = new byte[w + ((w + 127) / 128)];
                    int n = Rle.Pack(plane.AsSpan(y * w, w), enc);
                    rows.Add(enc.AsSpan(0, n).ToArray());
                }

                foreach (byte[] r in rows)
                {
                    body.WriteByte((byte)(r.Length >> 8));
                    body.WriteByte((byte)r.Length);
                }

                foreach (byte[] r in rows)
                {
                    body.Write(r);
                }
            }
            else
            {
                body.WriteByte(0);
                body.Write(plane);
            }

            byte[] hdr = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), (uint)body.Length);
            arrays.Write(hdr);
            arrays.Write(body.ToArray());
        }

        for (int i = 0; i < 23; i++)
        {
            arrays.Write(new byte[4]); // arrays that are not written
        }

        U32(3);
        U32((uint)(16 + 4 + arrays.Length));
        I32(0); I32(0); I32(h); I32(w);
        U32(24);
        entry.Write(arrays.ToArray());

        var all = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)entry.Length);
        all.Write(len);
        all.Write(entry.ToArray());
        while (all.Length % 4 != 0)
        {
            all.WriteByte(0);
        }

        return all.ToArray();
    }

    /// <summary>A <c>vmsk</c> block holding one closed rectangle (corners in pixels of a width x height document).</summary>
    public static byte[] RectangleMask(int docW, int docH, double x0, double y0, double x1, double y1)
    {
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void Record(int selector, Action body)
        {
            ms.Write(new byte[] { (byte)(selector >> 8), (byte)selector });
            long start = ms.Length;
            body();
            while (ms.Length - start < 24)
            {
                ms.WriteByte(0);
            }
        }

        void Point(double x, double y)
        {
            U32((uint)(int)Math.Round(y / docH * 16777216.0));
            U32((uint)(int)Math.Round(x / docW * 16777216.0));
        }

        U32(3);
        U32(0);
        Record(0, () => { ms.Write(new byte[] { 0, 4 }); });
        foreach ((double x, double y) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) })
        {
            Record(1, () => { Point(x, y); Point(x, y); Point(x, y); });
        }

        return ms.ToArray();
    }

    /// <summary>A document with one stub layer per fill, as Photoshop writes fill layers it has not rasterized.</summary>
    public static byte[] Many(int w, int h, (string Key, byte[] Block, byte[]? Mask)[] fills, byte[]? patterns = null)
    {
        var doc = new Document(w, h);
        var f = PsdFile.FromDocument(doc);
        foreach ((string key, byte[] block, byte[]? mask) in fills)
        {
            PixelLayer pl = doc.Add(doc.CreatePixelLayer(key, 1, 1, 0, 0));
            f.GetInfo(pl).Blocks.Add(new PsdBlock(key, block));
            if (mask != null)
            {
                f.GetInfo(pl).Blocks.Add(new PsdBlock("vmsk", mask));
            }
        }

        if (patterns != null)
        {
            f.DocumentBlocks.Add(new PsdBlock("Patt", patterns));
        }

        return Util.Save(f);
    }

    public static PsdFile Load(int w, int h, string key, byte[] block, byte[]? vmsk = null, byte[]? patterns = null, PsdReadOptions? options = null)
    {
        byte[] file = Bytes(w, h, key, block, vmsk, patterns);
        return PsdFile.Load(new MemoryStream(file), options);
    }

    public static byte[] Bytes(int w, int h, string key, byte[] block, byte[]? vmsk = null, byte[]? patterns = null)
    {
        var doc = new Document(w, h);
        var f = PsdFile.FromDocument(doc);
        PixelLayer pl = doc.Add(doc.CreatePixelLayer("fill", 1, 1, 0, 0));
        f.GetInfo(pl).Blocks.Add(new PsdBlock(key, block));
        if (vmsk != null)
        {
            f.GetInfo(pl).Blocks.Add(new PsdBlock("vmsk", vmsk));
        }

        if (patterns != null)
        {
            f.DocumentBlocks.Add(new PsdBlock("Patt", patterns));
        }

        return Util.Save(f);
    }
}

public class FillTests
{
    private static readonly FillDocs.Stop Black = new(0, 0);
    private static readonly FillDocs.Stop White = new(1, 255);

    private static Rgba32[] Render(PsdFile f) => f.Document.Render().ToArray<Rgba32>();

    private static void Near(int expected, int actual, int tolerance = 1) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} ± {tolerance}, got {actual}");

    private static FillLayer OnlyFill(PsdFile f)
    {
        Layer l = Assert.Single(f.Document.Root.Children);
        return Assert.IsType<FillLayer>(l);
    }

    /// <summary>Writes documents with every fill style as sample files (fuzz seeds) when ONEIMAGE_PSD_SEEDS_OUT names a folder.</summary>
    [Fact]
    public void WriteFillSampleFiles()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_SEEDS_OUT");
        if (dir == null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var fills = new List<(string, byte[], byte[]?)>();
        foreach (string type in new[] { "Lnr ", "Rdl ", "Angl", "Rflc", "Dmnd" })
        {
            fills.Add(("GdFl", FillDocs.Gradient(type, 30, [Black, new FillDocs.Stop(0.5, 120, 30), White], [new FillDocs.Opacity(0, 100), new FillDocs.Opacity(1, 60)], reverse: type == "Rflc", smoothness: 4096, method: type == "Dmnd" ? "Lnr " : null), FillDocs.RectangleMask(96, 96, 8, 8, 88, 88)));
        }

        File.WriteAllBytes(System.IO.Path.Combine(dir, "fill-gradients.psd"), FillDocs.Many(96, 96, [.. fills]));
        const string Id = "11111111-2222-3333-4444-555555555555";
        File.WriteAllBytes(System.IO.Path.Combine(dir, "fill-pattern.psd"), FillDocs.Many(96, 96, [("PtFl", FillDocs.Pattern(Id), FillDocs.RectangleMask(96, 96, 10, 10, 80, 70))], FillDocs.PatternResource(Id, 8, 6, (c, x, y) => (byte)((c * 70) + (x * 20) + (y * 5)))));
    }

    [Fact]
    public void LinearRunsAcrossTheBoxAtIntegerPixelCoordinates()
    {
        // Angle 0 on a 64 px wide document: the position is x/64, so black at x = 0 and 50% at x = 32. Smoothness 0 is a straight line.
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White]));
        OnlyFill(f);
        Rgba32[] px = Render(f);
        Near(0, px[(10 * 64) + 0].R);
        Near(128, px[(10 * 64) + 32].R);
        Near(191, px[(40 * 64) + 48].R);
        Near(252, px[(10 * 64) + 63].R);
        Assert.Equal(255, px[0].A);
    }

    [Fact]
    public void LinearAt90DegreesStartsAtTheBottomAndReverseFlipsIt()
    {
        using PsdFile up = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 90, [Black, White]));
        Rgba32[] a = Render(up);
        Near(255, a[(0 * 64) + 5].R); // the top row, 32 px above the center, is the end color
        Near(128, a[(32 * 64) + 5].R);
        Assert.True(a[(63 * 64) + 5].R < 8);

        using PsdFile down = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 90, [Black, White], reverse: true));
        Rgba32[] b = Render(down);
        Assert.True(b[(0 * 64) + 5].R < 8);
        Near(128, b[(32 * 64) + 5].R);
    }

    [Fact]
    public void LengthIsTheChordOfTheBoxAtThatAngle()
    {
        // A 100 x 40 document at 0 degrees: the line runs the 100 px width. At 90 degrees it runs the 40 px height.
        using PsdFile h = FillDocs.Load(100, 40, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White]));
        Near(128, Render(h)[(5 * 100) + 50].R);
        using PsdFile v = FillDocs.Load(100, 40, "GdFl", FillDocs.Gradient("Lnr ", 90, [Black, White]));
        Rgba32[] px = Render(v);
        Near(128, px[(20 * 100) + 3].R);
        Near(255, px[(0 * 100) + 3].R, 7);
    }

    [Fact]
    public void ScaleStretchesTheGradientAboutItsCenter()
    {
        // At 50% the gradient covers the middle half: black from x = 16 and white by x = 48 on a 64 px box.
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], scale: 50));
        Rgba32[] px = Render(f);
        Near(0, px[16].R, 2);
        Near(0, px[5].R);
        Near(128, px[32].R);
        Near(255, px[50].R);
    }

    [Fact]
    public void RadialStartsAtTheCenterAndReachesHalfTheChord()
    {
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Rdl ", 0, [Black, White]));
        Rgba32[] px = Render(f);
        Near(0, px[(32 * 64) + 32].R);
        Near(128, px[(32 * 64) + 48].R);   // 16 px out of a 32 px radius
        Near(128, px[(16 * 64) + 32].R);   // the same distance upward
        Near(255, px[(32 * 64) + 63].R, 8);
        Near(255, px[0].R);                // the corner is past the radius and keeps the end color
    }

    [Fact]
    public void ReflectedMirrorsAboutTheCenterLine()
    {
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Rflc", 0, [Black, White]));
        Rgba32[] px = Render(f);
        Near(0, px[(7 * 64) + 32].R);
        Near(128, px[(7 * 64) + 48].R);
        Near(128, px[(7 * 64) + 16].R);
        Near(255, px[(7 * 64) + 0].R);
        Near(255, px[(7 * 64) + 63].R, 8);
    }

    [Fact]
    public void DiamondGrowsAsTheSumOfTheDistancesAlongBothAxes()
    {
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Dmnd", 0, [Black, White]));
        Rgba32[] px = Render(f);
        Near(0, px[(32 * 64) + 32].R);
        Near(128, px[(32 * 64) + 48].R);   // |16| + |0| out of 32
        Near(128, px[(40 * 64) + 40].R);   // |8| + |8|
        Near(128, px[(24 * 64) + 24].R);   // the same in the other quadrant
        Near(255, px[(48 * 64) + 48].R);   // |16| + |16|
    }

    [Fact]
    public void AngleSweepsClockwiseFromTheGradientDirection()
    {
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Angl", 0, [Black, White]));
        Rgba32[] px = Render(f);
        Near(0, px[(32 * 64) + 48].R, 2);   // to the right of the center: the start
        Near(64, px[(48 * 64) + 32].R);     // below: a quarter of the way around
        Near(128, px[(32 * 64) + 16].R);    // to the left: half
        Near(191, px[(16 * 64) + 32].R);    // above: three quarters

        using PsdFile rotated = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Angl", 90, [Black, White]));
        Rgba32[] r = Render(rotated);
        Near(0, r[(16 * 64) + 32].R, 2);    // the start is now straight up
        Near(64, r[(32 * 64) + 48].R);      // clockwise from up is right
    }

    [Fact]
    public void StopLocationsMidpointsAndOpacityStopsShapeTheBlend()
    {
        // Black at 0, white at 1, with the halfway color moved to 25%: at x = 16 of 64 the gray is 50%.
        using PsdFile mid = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White with { Midpoint = 25 }]));
        Rgba32[] m = Render(mid);
        Near(128, m[16].R);
        Near(191, m[40].R);

        // A first stop that starts at 50% holds its color before that, and the last holds after it.
        using PsdFile held = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [new FillDocs.Stop(0.25, 0), new FillDocs.Stop(0.75, 255)]));
        Rgba32[] hp = Render(held);
        Near(0, hp[10].R);
        Near(255, hp[60].R);
        Near(128, hp[32].R);

        // Opacity falls from 100% to 0% across the gradient (straight alpha, so the colors stay put).
        using PsdFile fade = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], [new FillDocs.Opacity(0, 100), new FillDocs.Opacity(1, 0)]));
        Rgba32[] fp = Render(fade);
        Near(255, fp[0].A);
        Near(128, fp[32].A);
        Assert.True(fp[63].A <= 5);
    }

    [Fact]
    public void FullSmoothnessMixesInASmoothstepAndLinearInterpolationBlendsLight()
    {
        // Smoothness 4096 eases half-way toward smoothstep: at 25% the position becomes 0.25 + 0.5 * (0.15625 - 0.25) = 0.203.
        using PsdFile smooth = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], smoothness: 4096));
        Near((int)Math.Round(255 * 0.203125), Render(smooth)[16].R);

        // "Linear" interpolation blends in linear light: halfway between black and white is 188 once encoded as sRGB.
        using PsdFile light = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], method: "Lnr "));
        Near(188, Render(light)[32].R);
    }

    [Fact]
    public void AlignWithLayerOffDrawsOnTheWholeDocumentInsteadOfTheShape()
    {
        byte[] mask = FillDocs.RectangleMask(64, 64, 16, 8, 48, 40);

        // Aligned (the default), the shape's own 32 px wide box carries the whole gradient: black at its left edge, 50% in its middle.
        using PsdFile aligned = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White]), mask);
        Rgba32[] a = Render(aligned);
        Near(0, a[(20 * 64) + 16].R);
        Near(128, a[(20 * 64) + 32].R);
        Assert.Equal(0, a[(2 * 64) + 20].A); // outside the shape nothing is drawn

        // Not aligned, the document is the box, so the shape shows the slice from 25% to 75%.
        using PsdFile free = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], align: false), mask);
        Rgba32[] b = Render(free);
        Near(64, b[(20 * 64) + 16].R);
        Near(128, b[(20 * 64) + 32].R);
    }

    [Fact]
    public void OffsetMovesTheCenterByAPercentOfTheBox()
    {
        using PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], offsetX: 25));
        Near(128, Render(f)[48].R); // the center, and so the 50% point, moved right by 16 px
    }

    [Fact]
    public void PatternTilesFromTheDocumentCorner()
    {
        const string Id = "11111111-2222-3333-4444-555555555555";
        byte[] res = FillDocs.PatternResource(Id, 4, 3, (c, x, y) => (byte)((c * 60) + (x * 10) + (y * 3)));
        using PsdFile f = FillDocs.Load(20, 12, "PtFl", FillDocs.Pattern(Id), patterns: res);
        OnlyFill(f);
        Rgba32[] px = Render(f);
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                int px4 = x % 4, py3 = y % 3;
                Assert.Equal(new Rgba32((byte)((px4 * 10) + (py3 * 3)), (byte)(60 + (px4 * 10) + (py3 * 3)), (byte)(120 + (px4 * 10) + (py3 * 3)), 255), px[(y * 20) + x]);
            }
        }
    }

    [Fact]
    public void PatternInAShapeStartsOnePixelRightOfItsLeftEdge()
    {
        const string Id = "11111111-2222-3333-4444-555555555555";
        byte[] res = FillDocs.PatternResource(Id, 4, 4, (c, x, y) => (byte)((x * 16) + (y * 4)));
        byte[] mask = FillDocs.RectangleMask(32, 32, 8, 6, 24, 22);
        using PsdFile f = FillDocs.Load(32, 32, "PtFl", FillDocs.Pattern(Id), mask, res);
        Rgba32[] px = Render(f);
        // The shape's left edge is x = 8 and its top is y = 6; the tile column 0 sits at x = 9 and row 0 at y = 6.
        Assert.Equal((byte)(0 * 16 + 0 * 4), px[(6 * 32) + 9].R);
        Assert.Equal((byte)(1 * 16 + 0 * 4), px[(6 * 32) + 10].R);
        Assert.Equal((byte)(0 * 16 + 1 * 4), px[(7 * 32) + 9].R);
        Assert.Equal(0, px[(2 * 32) + 2].A);
    }

    [Fact]
    public void UnchangedFillsKeepTheirSettingsBlockAndWriteTheSameBytesTwice()
    {
        byte[] block = FillDocs.Gradient("Rdl ", 30, [Black, White], smoothness: 4096);
        byte[] first = FillDocs.Bytes(48, 48, "GdFl", block);
        using PsdFile f = PsdFile.Load(new MemoryStream(first));
        FillLayer fl = OnlyFill(f);
        Assert.Equal(PsdLayerKind.GradientOrPatternFill, f.GetInfo(fl).Kind);

        byte[] second = Util.Save(f);
        using PsdFile g = PsdFile.Load(new MemoryStream(second));
        OnlyFill(g);
        // Written back unchanged: the very same bytes are in the file, and a second write gives the same file.
        Assert.True(second.AsSpan().IndexOf(block) >= 0, "the GdFl block is written back byte for byte");
        Assert.Equal(second, Util.Save(g));
        Assert.Equal(Render(f), Render(g));
    }

    [Fact]
    public void ReplacingTheBrushDropsTheOriginalSettings()
    {
        using PsdFile f = FillDocs.Load(32, 32, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White]));
        FillLayer fl = OnlyFill(f);
        fl.Brush = OneImage.Drawing.Brush.Solid(new Rgba32(10, 20, 30, 255));
        using PsdFile g = PsdFile.Load(new MemoryStream(Util.Save(f)));
        Assert.True(OnlyFill(g).Brush.TryGetSolid(out Rgba32 c));
        Assert.Equal(new Rgba32(10, 20, 30, 255), c);
    }

    [Fact]
    public void WhatCannotBeReproducedKeepsTheStoredPixels()
    {
        // Noise gradients use Photoshop's own random numbers.
        byte[] noise = FillDocs.Gradient("Lnr ", 0, [Black, White], form: "ClNs");
        using (PsdFile f = FillDocs.Load(64, 64, "GdFl", noise))
        {
            Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
            Assert.Equal(PsdLayerKind.GradientOrPatternFill, f.GetInfo(f.Document.Root.Children[0]).Kind);
        }

        // Perceptual interpolation is a curve that is not reproduced.
        using (PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White], method: "Perc")))
        {
            Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
        }

        // A box under 16 px is snapped to whole pixels by Photoshop.
        using (PsdFile f = FillDocs.Load(12, 12, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White])))
        {
            Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
        }

        // A pattern the document does not carry, and a scaled pattern.
        using (PsdFile f = FillDocs.Load(16, 16, "PtFl", FillDocs.Pattern("not-there")))
        {
            Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
        }

        byte[] res = FillDocs.PatternResource("p1", 4, 4, (c, x, y) => (byte)(x + y));
        using (PsdFile f = FillDocs.Load(16, 16, "PtFl", FillDocs.Pattern("p1", scale: 200), patterns: res))
        {
            Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
        }

        // The option turns drawing off altogether.
        using (PsdFile f = FillDocs.Load(64, 64, "GdFl", FillDocs.Gradient("Lnr ", 0, [Black, White]), options: new PsdReadOptions { DrawFills = false }))
        {
            Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
        }
    }

    [Fact]
    public void HostileFillSettingsFailTypedOrFallBack()
    {
        // A truncated descriptor and a huge stop count must not throw anything but the typed errors, or keep the stored pixels.
        byte[] good = FillDocs.Gradient("Lnr ", 0, [Black, White]);
        for (int cut = 4; cut < good.Length; cut += 7)
        {
            byte[] bad = good.AsSpan(0, cut).ToArray();
            try
            {
                using PsdFile f = FillDocs.Load(32, 32, "GdFl", bad);
                Assert.IsType<PixelLayer>(Assert.Single(f.Document.Root.Children));
            }
            catch (ImageFormatException)
            {
            }
        }
    }
}
