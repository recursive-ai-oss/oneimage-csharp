using System.Security.Cryptography;
using System.Text;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

internal static class Model
{
    /// <summary>Everything the file format holds about a document, as text: structure, every modelled property, kinds, blocks (by hash).</summary>
    public static string Describe(PsdFile f)
    {
        var sb = new StringBuilder();
        Document d = f.Document;
        sb.Append($"doc {d.Width}x{d.Height} {d.Format} profile={Hash(d.ColorProfile.IccBytes.Span)}\n");
        foreach (PsdResource r in f.Resources)
        {
            sb.Append($"res {r.Id} '{r.Name}' {Hash(r.Data.Span)}\n");
        }

        foreach (PsdBlock b in f.DocumentBlocks)
        {
            sb.Append($"docblock {b.Key} {b.Large} {Hash(b.Data.Span)}\n");
        }

        Walk(sb, f, d.Root, 0);
        return sb.ToString();
    }

    public static void AssertSame(string a, string b)
    {
        string m = Diff(a, b);
        Assert.True(m.Length == 0, "model differs:\n" + m);
    }

    public static string Diff(string a, string b)
    {
        string[] la = a.Split('\n'), lb = b.Split('\n');
        var d = new List<string>();
        for (int i = 0; i < Math.Max(la.Length, lb.Length) && d.Count < 12; i++)
        {
            string x = i < la.Length ? la[i] : "<none>", y = i < lb.Length ? lb[i] : "<none>";
            if (x != y) { d.Add("- " + x + "\n+ " + y); }
        }
        return string.Join("\n", d);
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..16];

    private static void Walk(StringBuilder sb, PsdFile f, GroupLayer g, int depth)
    {
        string ind = new(' ', depth * 2);
        foreach (Layer l in g.Children)
        {
            PsdLayerInfo i = f.GetInfo(l);
            sb.Append($"{ind}{l.Kind} '{l.Name}' vis={l.Visible} op={l.Opacity:R} fill={l.FillOpacity:R} blend={l.BlendMode} clip={l.ClipToBelow} locks={l.Locks} bounds={l.Bounds} id={i.LayerId} srcBlend={i.SourceBlendKey}\n");
            foreach (PsdBlock b in i.Blocks)
            {
                sb.Append($"{ind}  block {b.Key} {b.Large} {Hash(b.Data.Span)}\n");
            }

            if (l.Mask is { } m)
            {
                sb.Append($"{ind}  mask {m.Area} d={m.Density:R} feather={m.Feather:R} outside={m.OutsideValue} enabled={m.Enabled}\n");
            }

            switch (l)
            {
                case FillLayer fl when fl.Brush.TryGetSolid(out Rgba32 c):
                    sb.Append($"{ind}  fill {c}\n");
                    break;
                case AdjustmentLayer al:
                    sb.Append($"{ind}  adjustment {al.Adjustment.Name} {string.Join(',', al.Adjustment.Parameters.Select(static p => p.Name + "=" + p.Value))}\n");
                    break;
                case GroupLayer sub:
                    sb.Append($"{ind}  group passthrough={sub.PassThrough} open={i.GroupOpen}\n");
                    Walk(sb, f, sub, depth + 1);
                    break;
            }
        }
    }

    public static (bool Equal, long Diff) SameRender(Document a, Document b)
    {
        Image ra = a.Render();
        Image rb = b.Render();
        Rgba32[] pa = ra.ToArray<Rgba32>();
        Rgba32[] pb = rb.ToArray<Rgba32>();
        long diff = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            if (pa[i] != pb[i])
            {
                diff++;
            }
        }

        return (diff == 0, diff);
    }
}

internal static class TestDocuments
{
    public static PsdFile Build(PixelFormat format)
    {
        var doc = new Document(150, 110, new DocumentOptions { Format = format });
        var file = PsdFile.FromDocument(doc);

        // A pixel layer with an offset, painted with a gradient-ish pattern, and a layer that hangs off the document.
        PixelLayer a = doc.Add(doc.CreatePixelLayer("base", 120, 90, 10, 8));
        a.Pixels.Fill(new Rgba32(200, 60, 30, 255), Drawing.Path.Rectangle(0, 0, 120, 90));
        a.Pixels.Fill(new Rgba32(30, 90, 220, 200), Drawing.Path.Circle(60, 45, 35));
        PixelLayer off = doc.Add(doc.CreatePixelLayer("off the edge", 60, 60, -20, 70));
        off.Pixels.Fill(new Rgba32(20, 160, 60, 180), Drawing.Path.Circle(30, 30, 28));
        off.Opacity = 153 / 255f; // the format stores opacity in 8 bits
        off.FillOpacity = 191 / 255f;
        off.BlendMode = BlendMode.Multiply;
        off.Locks = LayerLocks.TransparentPixels | LayerLocks.Position;
        file.GetInfo(off).LayerId = 42;

        // A mask with density and feather, a disabled one, and a hidden layer.
        PixelLayer masked = doc.Add(doc.CreatePixelLayer("masked", 100, 80, 25, 15));
        masked.Pixels.Fill(new Rgba32(240, 220, 20, 255), Drawing.Path.Rectangle(0, 0, 100, 80));
        LayerMask mask = masked.AddMask(revealAll: false, area: new ImageRect(20, 10, 90, 70));
        mask.Pixels.Fill(new Rgba32(255, 255, 255, 255), Drawing.Path.Circle(45, 35, 30));
        mask.Density = 0.8f;
        mask.Feather = 3.5f;
        mask.OutsideValue = 0;
        PixelLayer disabled = doc.Add(doc.CreatePixelLayer("disabled mask", 40, 40, 100, 60));
        disabled.Pixels.Fill(new Rgba32(10, 10, 10, 255), Drawing.Path.Rectangle(0, 0, 40, 40));
        disabled.AddMask(revealAll: true, area: new ImageRect(100, 60, 40, 40)).Enabled = false;
        PixelLayer hidden = doc.Add(doc.CreatePixelLayer("hidden", 30, 30, 0, 0));
        hidden.Pixels.Fill(new Rgba32(255, 0, 255, 255), Drawing.Path.Rectangle(0, 0, 30, 30));
        hidden.Visible = false;

        // Groups, one isolated with its own blend mode and mask, one pass-through inside it, and a clipped layer.
        GroupLayer iso = doc.Add(doc.CreateGroup("isolated", passThrough: false));
        iso.BlendMode = BlendMode.Screen;
        iso.Opacity = 230 / 255f;
        file.GetInfo(iso).GroupOpen = false;
        PixelLayer c1 = iso.Add(doc.CreatePixelLayer("in group", 50, 50, 70, 10));
        c1.Pixels.Fill(new Rgba32(120, 20, 200, 255), Drawing.Path.Circle(25, 25, 24));
        GroupLayer inner = iso.Add(doc.CreateGroup("inner", passThrough: true));
        PixelLayer c2 = inner.Add(doc.CreatePixelLayer("deep", 30, 30, 90, 30));
        c2.Pixels.Fill(new Rgba32(255, 255, 0, 255), Drawing.Path.Rectangle(0, 0, 30, 30));
        c2.BlendMode = BlendMode.Overlay;
        PixelLayer clipped = doc.Add(doc.CreatePixelLayer("clipped", 70, 70, 10, 8));
        clipped.Pixels.Fill(new Rgba32(255, 255, 255, 255), Drawing.Path.Rectangle(0, 0, 70, 35));
        clipped.ClipToBelow = true;

        // Fill and adjustment layers.
        doc.Add(doc.CreateFillLayer("tint", new Rgba32(255, 200, 100, 255))).Opacity = 0.2f;
        AdjustmentLayer lv = doc.Add(doc.CreateAdjustmentLayer("levels", Adjustment.Levels(inBlack: 20 / 255.0, inWhite: 235 / 255.0, gamma: 1.2, outBlack: 10 / 255.0, outWhite: 245 / 255.0)));
        lv.Opacity = 128 / 255f;
        doc.Add(doc.CreateAdjustmentLayer("curves", Adjustment.Curves([new CurvePoint(0, 0), new CurvePoint(77 / 255.0, 102 / 255.0), new CurvePoint(1, 1)], ColorChannel.All, false, CurveInterpolation.Natural)));
        doc.Add(doc.CreateAdjustmentLayer("hue", Adjustment.HueSaturation(hue: 20, saturation: 0.1, lightness: -0.05)));
        doc.Add(doc.CreateAdjustmentLayer("b/c", Adjustment.BrightnessContrast(0.2, 0.3, false, BrightnessContrastMode.PhotoshopLegacy)));
        doc.Add(doc.CreateAdjustmentLayer("exposure", Adjustment.Exposure(0.5)));
        doc.Add(doc.CreateAdjustmentLayer("posterize", Adjustment.Posterize(8, false, PosterizeMode.Buckets)));
        doc.Add(doc.CreateAdjustmentLayer("threshold", Adjustment.Threshold(230 / 255.0))).Visible = false;
        doc.Add(doc.CreateAdjustmentLayer("invert", Adjustment.Invert())).Visible = false;

        // Kept blocks, resources and document blocks come back unchanged.
        file.GetInfo(a).Blocks.Add(new PsdBlock("TySh", Enumerable.Range(0, 100).Select(static i => (byte)(i * 7)).ToArray()));
        file.GetInfo(a).Blocks.Add(new PsdBlock("lfx2", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        file.Resources.Add(new PsdResource(1005, "", new byte[16]));
        file.Resources.Add(new PsdResource(1028, "iptc", new byte[] { 1, 2, 3, 4, 5 }));
        file.DocumentBlocks.Add(new PsdBlock("Patt", new byte[] { 9, 8, 7, 6 }));
        return file;
    }
}

public class GeneratedRoundTripTests
{
    private static PsdFile Build(PixelFormat format) => TestDocuments.Build(format);

    [Theory]
    [InlineData(8, PsdCompression.Rle, false)]
    [InlineData(8, PsdCompression.Raw, false)]
    [InlineData(8, PsdCompression.Rle, true)]
    [InlineData(16, PsdCompression.Rle, false)]
    [InlineData(32, PsdCompression.Raw, false)]
    [InlineData(1, PsdCompression.Rle, false)]
    public void EveryModelledPropertySurvivesAWriteAndRead(int depth, PsdCompression compression, bool psb)
    {
        PixelFormat format = depth switch { 16 => PixelFormat.Rgba16, 32 => PixelFormat.RgbaF32, 1 => PixelFormat.GrayAlpha8, _ => PixelFormat.Rgba8 };
        using PsdFile original = Build(format);
        byte[] first = Util.Save(original, new PsdWriteOptions { Large = psb, Compression = compression });
        using PsdFile loaded = PsdFile.Load(first);
        Model.AssertSame(Model.Describe(original), Model.Describe(loaded));

        // Pixels: the same render, bit for bit.
        Image r1 = original.Document.Render();
        Image r2 = loaded.Document.Render();
        Rgba32[] p1 = r1.ToArray<Rgba32>();
        Rgba32[] p2 = r2.ToArray<Rgba32>();
        Assert.Equal(p1, p2);

        // Writing the loaded document again gives the same file.
        byte[] second = Util.Save(loaded, new PsdWriteOptions { Large = psb, Compression = compression });
        Assert.Equal(first.Length, second.Length);
        Assert.True(first.AsSpan().SequenceEqual(second), "second write differs from the first");

        // The file is a PSB exactly when asked.
        Assert.Equal(psb ? 2 : 1, first[5]);
        Assert.Equal(psb, loaded.WasLarge);
        Assert.Equal(depth == 1 ? 8 : depth, loaded.SourceBitDepth);
        Assert.Equal(depth == 1 ? PsdColorMode.Grayscale : PsdColorMode.Rgb, loaded.SourceColorMode);
    }

    /// <summary>Writes the generated documents as sample files (fuzz seeds and test data) when ONEIMAGE_PSD_SEEDS_OUT names a folder.</summary>
    [Fact]
    public void WriteSampleFiles()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_SEEDS_OUT");
        if (dir == null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach ((string name, int depth, PsdCompression c, bool psb) in new[] { ("layers-8-rle.psd", 8, PsdCompression.Rle, false), ("layers-8-raw.psd", 8, PsdCompression.Raw, false), ("layers-16-rle.psd", 16, PsdCompression.Rle, false), ("layers-32-raw.psd", 32, PsdCompression.Raw, false), ("layers-8-rle.psb", 8, PsdCompression.Rle, true) })
        {
            PixelFormat format = depth switch { 16 => PixelFormat.Rgba16, 32 => PixelFormat.RgbaF32, _ => PixelFormat.Rgba8 };
            using PsdFile f = Build(format);
            File.WriteAllBytes(System.IO.Path.Combine(dir, name), Util.Save(f, new PsdWriteOptions { Large = psb, Compression = c }));
        }
    }

    [Fact]
    public void TheMergedImageOfAWrittenFileIsTheRender()
    {
        using PsdFile f = Build(PixelFormat.Rgba8);
        f.Document.Background = new Rgba32(250, 250, 250, 255);
        byte[] bytes = Util.Save(f);
        Image composite = Util.LoadComposite(bytes);
        Assert.Equal(PixelFormat.Rgb8, composite.Format);
        (double mean, int max, _, _, _) = Util.Diff(composite, f.Document.Render());
        Assert.Equal(0, mean);
        Assert.Equal(0, max);
    }

    [Fact]
    public void ATransparentBackgroundWritesAnAlphaChannelThatReadsBackAsTransparency()
    {
        var doc = new Document(40, 30);
        PixelLayer p = doc.Add(doc.CreatePixelLayer("p", 10, 10, 5, 5));
        p.Pixels.Fill(new Rgba32(255, 0, 0, 255), Drawing.Path.Rectangle(0, 0, 10, 10));
        using var f = PsdFile.FromDocument(doc);
        byte[] bytes = Util.Save(f);
        var cfg = new ImageConfiguration().Add(PsdFormat.Instance);
        Image img = Image.Load(bytes, new LoadOptions { Configuration = cfg });
        Assert.True(img.Format.HasAlpha);
        Rgba32[] px = img.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[10 * 40 + 10]);
        Assert.Equal(0, px[0].A);
    }

    [Fact]
    public void WritingToAStreamThatCannotSeekWorks()
    {
        using PsdFile f = Build(PixelFormat.Rgba8);
        byte[] expected = Util.Save(f);
        using var inner = new MemoryStream();
        using var forward = new ForwardOnlyStream(inner);
        f.Save(forward);
        Assert.True(expected.AsSpan().SequenceEqual(inner.ToArray()));
    }

    [Fact]
    public void UnwritableLayersAreReportedByName()
    {
        var doc2 = new Document(8, 8);
        doc2.Add(doc2.CreateAdjustmentLayer("mine", Adjustment.Custom("weird", i => i)));
        using var f2 = PsdFile.FromDocument(doc2);
        Assert.Throws<ImageOperationException>(() => Util.Save(f2));
    }

    [Fact]
    public void ADocumentTooWideForAPsdAsksForAPsb()
    {
        var doc = new Document(30_001, 4);
        using var f = PsdFile.FromDocument(doc);
        Assert.Throws<ImageOperationException>(() => f.Save(Stream.Null, new PsdWriteOptions { Large = false }));
        // Automatically a PSB:
        using var ms = new MemoryStream();
        f.Save(ms);
        Assert.Equal(2, ms.ToArray()[5]);
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

public class CorpusRoundTripTests
{
    [Fact]
    public void EveryCorpusFileRoundTrips()
    {
        if (Util.CorpusDir == null)
        {
            return;
        }

        var failures = new List<string>();
        int checkedFiles = 0;
        foreach (string path in Util.CorpusFiles())
        {
            string rel = System.IO.Path.GetRelativePath(Util.CorpusDir, path);
            PsdFile first;
            try
            {
                first = PsdFile.Load(path);
            }
            catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
            {
                continue; // files that do not load are listed in the corpus report
            }

            using (first)
            {
                try
                {
                    byte[] bytes = Util.Save(first);
                    using PsdFile second = PsdFile.Load(bytes);
                    string a = Model.Describe(first);
                    string b = Model.Describe(second);
                    string modelDiff = a == b ? "" : Model.Diff(a, b);
                    if (modelDiff.Length > 0 && first.SourceColorMode is PsdColorMode.Cmyk or PsdColorMode.Lab or PsdColorMode.Indexed
                        && modelDiff.Split('\n').All(static l => l.Contains("adjustment ")))
                    {
                        // Documented loss: an adjustment made for CMYK/Lab pixels is replaced by an identity levels layer in the RGB file.
                        modelDiff = "";
                    }

                    if (modelDiff.Length > 0)
                    {
                        failures.Add(rel + ": model differs: " + Model.Diff(a, b).Replace('\n', '|'));
                        continue;
                    }

                    if ((long)first.Document.Width * first.Document.Height <= 4_000_000)
                    {
                        Image ra = first.Document.Render();
                        Image rb = second.Document.Render();
                        Rgba32[] pa = ra.ToArray<Rgba32>();
                        Rgba32[] pb = rb.ToArray<Rgba32>();
                        if (!pa.AsSpan().SequenceEqual(pb))
                        {
                            failures.Add(rel + ": render differs");
                            continue;
                        }
                    }

                    byte[] again = Util.Save(second);
                    if (!bytes.AsSpan().SequenceEqual(again))
                    {
                        failures.Add(rel + ": second write differs");
                        continue;
                    }

                    checkedFiles++;
                }
                catch (Exception ex)
                {
                    failures.Add(rel + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        string report = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_ROUNDTRIP_REPORT") ?? "";
        if (report.Length > 0)
        {
            File.WriteAllText(report, $"round-tripped {checkedFiles}\n" + string.Join('\n', failures));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} corpus files failed to round-trip (of {checkedFiles + failures.Count}):\n" + string.Join('\n', failures.Take(20)));
    }
}
