using OneImage.Drawing;
using OneImage.Layers;
using IoPath = System.IO.Path;
using Path = OneImage.Drawing.Path;
using static OneImage.Tests.Layers.LayerCompositeTests;

namespace OneImage.Tests.Layers;

public class LayerSemanticsTests
{
    private static readonly Rgba32 Bg = new(200, 100, 50, 255);

    [Fact]
    public void PassThroughGroupSeesBackdropIsolatedDoesNot()
    {
        var child = new Rgba32(100, 200, 255, 255);
        foreach (bool passThrough in new[] { true, false })
        {
            using var doc = new Document(130, 70, new DocumentOptions { Background = Bg });
            GroupLayer g = doc.Add(doc.CreateGroup("g", passThrough));
            PixelLayer l = AddPixels(doc, g, "c", Enumerable.Repeat(child, 130 * 70).ToArray(), 130, 70, 0, 0);
            l.BlendMode = BlendMode.Multiply;
            Image multiplied = Solid(130, 70, Bg).Composite(Solid(130, 70, child), 0, 0, 1f, false, BlendMode.Multiply);
            // Pass-through: Multiply sees the background. Isolated: Multiply sees a transparent surface, so the layer keeps its own color.
            AssertSame(Pixels(passThrough ? multiplied : Solid(130, 70, child)), Pixels(doc.Render()));

            if (!passThrough)
            {
                // The isolated surface itself can be multiplied into the backdrop through the group's blend mode.
                g.BlendMode = BlendMode.Multiply;
                AssertSame(Pixels(multiplied), Pixels(doc.Render()));
            }
        }
    }

    [Fact]
    public void FullStrengthPassThroughGroupEqualsFlatStack()
    {
        using var flat = new Document(260, 140, new DocumentOptions { Background = Bg });
        using var grouped = new Document(260, 140, new DocumentOptions { Background = Bg });
        Rgba32[] a = Noise(200, 100, 1);
        Rgba32[] b = Noise(150, 130, 2);
        AddPixels(flat, null, "a", a, 200, 100, 10, 10);
        AddPixels(flat, null, "b", b, 150, 130, 100, 5).BlendMode = BlendMode.SoftLight;
        GroupLayer g = grouped.Add(grouped.CreateGroup("g"));
        AddPixels(grouped, g, "a", a, 200, 100, 10, 10);
        AddPixels(grouped, g, "b", b, 150, 130, 100, 5).BlendMode = BlendMode.SoftLight;
        AssertSame(Pixels(flat.Render()), Pixels(grouped.Render()));
    }

    [Fact]
    public void PassThroughGroupOpacityMixesWithBackdrop()
    {
        using var doc = new Document(40, 40, new DocumentOptions { Background = new Rgba32(0, 0, 0, 255) });
        GroupLayer g = doc.Add(doc.CreateGroup("g"));
        AddPixels(doc, g, "w", Enumerable.Repeat(new Rgba32(200, 200, 200, 255), 1600).ToArray(), 40, 40, 0, 0);
        g.Opacity = 0.5f;
        Rgba32 p = Pixels(doc.Render())[0];
        Assert.InRange(p.R, 99, 101);
        Assert.Equal(255, p.A);
    }

    [Fact]
    public void MaskDensityFeatherAndOutside()
    {
        using var doc = new Document(128, 128, new DocumentOptions { Background = new Rgba32(0, 0, 255, 255) });
        PixelLayer red = AddPixels(doc, null, "r", Enumerable.Repeat(new Rgba32(255, 0, 0, 255), 128 * 128).ToArray(), 128, 128, 0, 0);
        LayerMask m = red.AddMask(revealAll: false);
        m.Pixels.Fill(new Rgba32(255, 255, 255, 255), Path.Rectangle(0, 0, 64, 128), antialias: false);

        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[10]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[100]);

        m.Density = 0.5f;
        px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[10]);
        Assert.InRange(px[100].R, 126, 129); // halfway between red and blue
        Assert.InRange(px[100].B, 126, 129);

        m.Density = 1f;
        m.Feather = 6f;
        px = Pixels(doc.Render());
        Rgba32 edge = px[64];
        Assert.InRange(edge.R, 80, 175);
        Assert.True(px[50].R > px[80].R);

        m.Feather = 0;
        m.Enabled = false;
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixels(doc.Render())[100]);
        m.Enabled = true;

        // Outside the mask canvas the outside value rules.
        PixelLayer small = AddPixels(doc, null, "s", Enumerable.Repeat(new Rgba32(0, 255, 0, 255), 128 * 128).ToArray(), 128, 128, 0, 0);
        LayerMask sm = small.AddMask(revealAll: true, area: new ImageRect(0, 0, 32, 128));
        sm.Pixels.Clear(new Rgba32(0, 0, 0, 255));
        px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(0, 255, 0, 255), px[100]); // outside value 255 shows the green layer
        Assert.NotEqual(new Rgba32(0, 255, 0, 255), px[10]); // inside the mask canvas it is black = hidden
        sm.OutsideValue = 0;
        Assert.NotEqual(new Rgba32(0, 255, 0, 255), Pixels(doc.Render())[100]);
    }

    [Fact]
    public void ClippingGroupConfinesLayersToTheBase()
    {
        using var doc = new Document(128, 64, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        var half = new Rgba32[128 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                half[(y * 128) + x] = new Rgba32(0, 0, 255, 255);
            }
        }

        PixelLayer b = AddPixels(doc, null, "base", half, 128, 64, 0, 0);
        PixelLayer c = AddPixels(doc, null, "clip", Enumerable.Repeat(new Rgba32(0, 255, 0, 255), 128 * 64).ToArray(), 128, 64, 0, 0);
        c.ClipToBelow = true;
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(0, 255, 0, 255), px[10]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), px[100]);

        // The base's opacity applies to the base and its clipped layers together.
        b.Opacity = 0.5f;
        px = Pixels(doc.Render());
        Assert.InRange(px[10].R, 126, 129);
        Assert.InRange(px[10].G, 254, 255);
        Assert.Equal(new Rgba32(255, 255, 255, 255), px[100]);

        // A hidden base hides what is clipped to it.
        b.Visible = false;
        Assert.Equal(new Rgba32(255, 255, 255, 255), Pixels(doc.Render())[10]);

        // A clipped layer at the bottom has no base and is drawn normally.
        using var doc2 = new Document(16, 16, new DocumentOptions { Background = Bg });
        PixelLayer only = AddPixels(doc2, null, "only", Enumerable.Repeat(new Rgba32(1, 2, 3, 255), 256).ToArray(), 16, 16, 0, 0);
        only.ClipToBelow = true;
        Assert.Equal(new Rgba32(1, 2, 3, 255), Pixels(doc2.Render())[0]);
    }

    [Fact]
    public void ClippedAdjustmentAffectsOnlyTheBase()
    {
        using var doc = new Document(64, 32, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        var half = new Rgba32[64 * 32];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                half[(y * 64) + x] = new Rgba32(10, 20, 30, 255);
            }
        }

        AddPixels(doc, null, "base", half, 64, 32, 0, 0);
        doc.Add(doc.CreateAdjustmentLayer("inv", Adjustment.Invert())).ClipToBelow = true;
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(245, 235, 225, 255), px[5]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), px[40]);
    }

    [Fact]
    public void AdjustmentAffectsOnlyWhatIsBelow()
    {
        using var doc = new Document(200, 150, new DocumentOptions { Background = Bg });
        Rgba32[] a = Noise(200, 150, 1, opaque: true);
        Rgba32[] b = Noise(80, 60, 2);
        AddPixels(doc, null, "a", a, 200, 150, 0, 0);
        AdjustmentLayer adj = doc.Add(doc.CreateAdjustmentLayer("inv", Adjustment.Invert()));
        AddPixels(doc, null, "b", b, 80, 60, 50, 40);
        Image expected = Solid(200, 150, Bg).Composite(Image.FromPixels<Rgba32>(a, 200, 150), 0, 0).Invert().Composite(Image.FromPixels<Rgba32>(b, 80, 60), 50, 40);
        AssertSame(Pixels(expected), Pixels(doc.Render()));

        // Half strength is the midpoint of the backdrop and its adjusted version.
        adj.Opacity = 0.5f;
        Rgba32 p = Pixels(doc.Render())[0];
        Rgba32 under = a[0];
        Assert.InRange(p.R, ((under.R + (255 - under.R)) / 2) - 2, ((under.R + (255 - under.R)) / 2) + 2);
    }

    [Fact]
    public void FillLayerAndLevelsAdjustment()
    {
        using var doc = new Document(150, 100);
        doc.Add(doc.CreateFillLayer("fill", new Rgba32(10, 20, 30, 255)));
        doc.Add(doc.CreateAdjustmentLayer("levels", Adjustment.Levels(inBlack: 0, inWhite: 0.5)));
        Rgba32 p = Pixels(doc.Render())[0];
        Assert.Equal(new Rgba32(20, 40, 60, 255), p);
        Assert.Equal("levels", doc.Root.Children[1].Describe().Parameters.First(x => x.Name == "adjustment").Value);
    }

    [Fact]
    public void GradientFillLayerAcrossTiles()
    {
        using var doc = new Document(300, 20);
        Brush g = Brush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(300, 0), [new ColorStop(0f, new Rgba32(0, 0, 0, 255)), new ColorStop(1f, new Rgba32(255, 255, 255, 255))]);
        FillLayer f = doc.Add(doc.CreateFillLayer("g", g));
        Rgba32[] px = Pixels(doc.Render());
        Assert.InRange(px[0].R, 0, 2);
        Assert.InRange(px[299].R, 253, 255);
        Assert.InRange(px[150].R, 125, 130);
        for (int x = 1; x < 300; x++)
        {
            Assert.True(px[x].R >= px[x - 1].R);
        }

        f.Opacity = 0.5f;
        Assert.InRange(Pixels(doc.Render())[299].A, 127, 129);
    }

    [Fact]
    public void SnapshotsAreImmutable()
    {
        using var doc = new Document(100, 100, new DocumentOptions { Background = Bg });
        PixelLayer l = AddPixels(doc, null, "a", Noise(100, 100, 3), 100, 100, 0, 0);
        DocumentSnapshot s1 = doc.Snapshot();
        Rgba32[] before = Pixels(s1.Render());
        l.Pixels.Fill(new Rgba32(1, 1, 1, 255), Path.Rectangle(0, 0, 100, 100), antialias: false);
        l.Opacity = 0.3f;
        doc.Background = new Rgba32(9, 9, 9, 255);
        DocumentSnapshot s2 = doc.Snapshot();
        AssertSame(before, Pixels(s1.Render()));
        Assert.NotEqual(before[0], Pixels(s2.Render())[0]);
        Assert.True(s2.Revision > s1.Revision);
    }

    [Fact]
    public void ReorderingAndRemovingChangeTheRender()
    {
        using var doc = new Document(64, 64, new DocumentOptions { Background = Bg });
        PixelLayer a = AddPixels(doc, null, "a", Enumerable.Repeat(new Rgba32(255, 0, 0, 255), 4096).ToArray(), 64, 64, 0, 0);
        PixelLayer b = AddPixels(doc, null, "b", Enumerable.Repeat(new Rgba32(0, 255, 0, 255), 4096).ToArray(), 64, 64, 0, 0);
        Assert.Equal(new Rgba32(0, 255, 0, 255), Pixels(doc.Render())[0]);
        doc.Root.Move(b, 0);
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixels(doc.Render())[0]);
        Assert.True(doc.Root.Remove(a));
        Assert.Equal(new Rgba32(0, 255, 0, 255), Pixels(doc.Render())[0]);
    }

    [Fact]
    public void GroupNestingIsBoundedAndOwnershipChecked()
    {
        using var doc = new Document(8, 8);
        GroupLayer g = doc.Root;
        for (int i = 0; i < GroupLayer.MaxDepth; i++)
        {
            g = g.Add(doc.CreateGroup("g" + i));
        }

        Assert.Throws<ArgumentException>(() => g.Add(doc.CreateGroup("too deep")));
        GroupLayer other = doc.CreateGroup("x");
        doc.Add(other);
        Assert.Throws<ArgumentException>(() => doc.Root.Add(other));
        using var doc2 = new Document(8, 8);
        Assert.Throws<ArgumentException>(() => doc.Root.Add(doc2.CreateGroup("foreign")));
        Assert.Throws<ArgumentException>(() => other.Add(other));
    }

    [Fact]
    public void DescriptionUsesNeutralNames()
    {
        using var doc = new Document(32, 16, new DocumentOptions { Background = new Rgba32(255, 0, 0, 255) });
        PixelLayer l = AddPixels(doc, null, "a", Noise(8, 8, 1), 8, 8, 3, 4);
        l.BlendMode = BlendMode.ColorDodge;
        l.Opacity = 0.25f;
        l.Locks = LayerLocks.Position | LayerLocks.Pixels;
        l.AddMask().Density = 0.5f;
        GroupLayer g = doc.Add(doc.CreateGroup("grp"));
        doc.Add(doc.CreateFillLayer("fill", Brush.Solid(new Rgba32(1, 2, 3, 255))));
        LayerDescription d = doc.Describe();
        Assert.Equal("document", d.Kind);
        Assert.Equal(3, d.Children.Count);
        LayerDescription pd = d.Children[0];
        object V(string n) => pd.Parameters.First(p => p.Name == n).Value;
        Assert.Equal("pixel", pd.Kind);
        Assert.Equal("color-dodge", V("blend"));
        Assert.Equal(0.25, V("opacity"));
        Assert.Equal(true, V("lock-position"));
        Assert.Equal(false, V("lock-transparent-pixels"));
        Assert.Equal(0.5, V("mask-density"));
        Assert.Equal(3L, V("x"));
        Assert.Equal("pass-through", d.Children[1].Parameters.First(p => p.Name == "blend").Value);
        Assert.Equal("group", d.Children[1].Kind);
        Assert.Contains(d.Children[2].Parameters, p => p.Name.StartsWith("brush.", StringComparison.Ordinal));
        Assert.All(d.Children.SelectMany(c => c.Parameters).Concat(d.Parameters), p => Assert.True(p.Value is long or double or bool or string));
        _ = g;
    }

    [Fact]
    public void PixelLayerFromImageConvertsAndCopies()
    {
        using var doc = new Document(100, 100, new DocumentOptions { Background = Bg });
        Rgba32[] px = Noise(60, 40, 7);
        PixelLayer l = doc.CreatePixelLayer("img", Image.FromPixels<Rgba32>(px, 60, 40), 10, 20);
        doc.Add(l);
        Image expected = Solid(100, 100, Bg).Composite(Image.FromPixels<Rgba32>(px, 60, 40), 10, 20);
        AssertSame(Pixels(expected), Pixels(doc.Render()));
    }

    [Fact]
    public void ParallelRendersOfSnapshotsWhileEditing()
    {
        using var doc = new Document(300, 300, new DocumentOptions { Background = Bg });
        PixelLayer l = AddPixels(doc, null, "a", Noise(300, 300, 1), 300, 300, 0, 0);
        var snaps = new List<DocumentSnapshot>();
        var expected = new List<Rgba32[]>();
        for (int i = 0; i < 12; i++)
        {
            l.Pixels.Fill(new Rgba32((byte)(i * 20), 5, 5, 255), Path.Rectangle(i * 20, i * 10, 50, 50), antialias: false);
            DocumentSnapshot s = doc.Snapshot();
            snaps.Add(s);
            expected.Add(Pixels(s.Render()));
        }

        var editor = Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                l.Pixels.Fill(new Rgba32((byte)i, 200, 5, 255), Path.Rectangle(i * 5, 100, 30, 30), antialias: false);
                doc.Snapshot();
            }
        });
        Parallel.For(0, 48, i =>
        {
            int k = i % snaps.Count;
            AssertSame(expected[k], Pixels(snaps[k].Render()));
        });
        editor.Wait();
    }
}

public class LayerIncrementalTests
{
    [Fact]
    public void SmallEditRecomputesOnlyDirtyTiles()
    {
        const int W = 1024, H = 1024;
        using var doc = new Document(W, H, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        PixelLayer target = null!;
        for (int i = 0; i < 6; i++)
        {
            PixelLayer l = AddPixels(doc, null, "l" + i, Noise(W, H, i, opaque: i == 0), W, H, 0, 0);
            l.Opacity = 0.6f;
            if (i == 3)
            {
                target = l;
            }
        }

        Rgba32[] first = Pixels(doc.Render());
        Assert.Equal(64, doc.TilesComputed);
        long before = doc.TilesComputed;

        // Identical re-render: everything from the cache.
        AssertSame(first, Pixels(doc.Render()));
        Assert.Equal(before, doc.TilesComputed);

        // A 20x20 patch inside one tile.
        target.Pixels.Fill(new Rgba32(255, 0, 0, 255), Path.Rectangle(300, 300, 20, 20), antialias: false);
        Rgba32[] second = Pixels(doc.Render());
        Assert.Equal(before + 1, doc.TilesComputed);
        Assert.NotEqual(first[(310 * W) + 310], second[(310 * W) + 310]);

        // A patch across a tile corner touches four tiles.
        before = doc.TilesComputed;
        target.Pixels.Fill(new Rgba32(0, 255, 0, 255), Path.Rectangle(250, 250, 20, 20), antialias: false);
        Pixels(doc.Render());
        Assert.Equal(before + 4, doc.TilesComputed);

        // A property change recomputes only the layer's area; moving a layer both old and new.
        before = doc.TilesComputed;
        PixelLayer small = doc.CreatePixelLayer("small", 100, 100, 500, 500);
        doc.Add(small);
        small.Pixels.Fill(new Rgba32(0, 0, 255, 255), Path.Rectangle(0, 0, 100, 100), antialias: false);
        Pixels(doc.Render());
        Assert.Equal(before + 4, doc.TilesComputed); // 500..599 spans tiles 3 and 4 in each axis

        // And the incremental result equals a from-scratch render of the same state.
        Image fresh = doc.Render();
        using var copy = new Document(W, H, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        foreach (Layer layer in doc.Root.Children)
        {
            var src = (PixelLayer)layer;
            var px = new Rgba32[src.Pixels.Width * src.Pixels.Height];
            src.Pixels.CopyTo<Rgba32>(px, new ImageRect(0, 0, src.Pixels.Width, src.Pixels.Height));
            PixelLayer c = AddPixels(copy, null, src.Name, px, src.Pixels.Width, src.Pixels.Height, src.X, src.Y);
            c.Opacity = src.Opacity;
        }

        AssertSame(Pixels(copy.Render()), Pixels(fresh));
    }
}

public class LayerSpillTests
{
    [Fact]
    public void ConcurrentReadersSeeStableTilesWhileTheStoreEvicts()
    {
        string dir = IoPath.Combine(IoPath.GetTempPath(), "oneimage-spill-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SpillTileStore(256 * 1024, dir, long.MaxValue);
            var tiles = new Tile[200];
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i] = Tile.Rent(Tile.ByteSize(PixelFormat.Rgba8), clear: false, store);
                tiles[i].Span.Fill((byte)(i + 1));
                tiles[i].Seal();
            }

            for (int round = 0; round < 4; round++)
            {
                Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
                {
                    var rng = new Random((round * 100) + worker);
                    for (int n = 0; n < 400; n++)
                    {
                        int i = rng.Next(tiles.Length);
                        Tile t = tiles[i].Retain();
                        ReadOnlySpan<byte> span = t.Span;
                        Assert.Equal((byte)(i + 1), span[0]);
                        Assert.Equal((byte)(i + 1), span[span.Length - 1]);
                        t.Release();
                    }
                });
            }

            SpillStatistics s = store.Statistics;
            Assert.True(s.TilesRead > 0 && s.TilesWritten > 0);
            foreach (Tile t in tiles)
            {
                t.Release();
            }

            Assert.Equal(0, store.Statistics.ResidentBytes);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void CancelledRendersDoNotLeakResidentBytes()
    {
        string dir = IoPath.Combine(IoPath.GetTempPath(), "oneimage-spill-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int W = 512, H = 512;
            using var doc = new Document(W, H, new DocumentOptions { Spill = new SpillOptions { BudgetBytes = 1L << 20, Directory = dir } });
            for (int i = 0; i < 8; i++)
            {
                AddPixels(doc, null, "l" + i, Noise(W, H, i), W, H, 0, 0).Flush();
            }

            var buffer = new Rgba32[W * H];
            for (int i = 0; i < 12; i++)
            {
                doc.Background = new Rgba32((byte)i, 0, 0, 255);
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(i % 4));
                try
                {
                    doc.Render().CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, W, H), 0, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            doc.Background = new Rgba32(1, 2, 3, 255);
            doc.Render().CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, W, H));
            SpillStatistics s = doc.SpillStatistics!.Value;
            Assert.True(s.ResidentBytes <= s.BudgetBytes + (2L << 20), $"resident {s.ResidentBytes}");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void SpillingDocumentRendersLikeMemoryDocumentAndCleansUp()
    {
        string dir = IoPath.Combine(IoPath.GetTempPath(), "oneimage-spill-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const int W = 384, H = 384;
            var spill = new SpillOptions { BudgetBytes = 1L << 20, Directory = dir };
            using var a = new Document(W, H, new DocumentOptions { Background = new Rgba32(5, 5, 5, 255), Spill = spill });
            using var b = new Document(W, H, new DocumentOptions { Background = new Rgba32(5, 5, 5, 255) });
            for (int i = 0; i < 14; i++)
            {
                Rgba32[] px = Noise(W, H, i, opaque: false);
                PixelLayer la = AddPixels(a, null, "l" + i, px, W, H, 0, 0);
                la.Flush();
                la.Opacity = 0.7f;
                PixelLayer lb = AddPixels(b, null, "l" + i, px, W, H, 0, 0);
                lb.Opacity = 0.7f;
            }

            Rgba32[] ra = Pixels(a.Render());
            AssertSame(Pixels(b.Render()), ra);
            SpillStatistics s = a.SpillStatistics!.Value;
            Assert.True(s.TilesWritten > 0);
            Assert.True(s.TilesRead > 0);
            Assert.True(s.PeakResidentBytes < s.BudgetBytes + (3L << 20), $"peak {s.PeakResidentBytes}");
            Assert.Null(b.SpillStatistics);

            // Edit after tiles were spilled: only the edited layer changes, the rest comes back from disk.
            PixelLayer top = (PixelLayer)a.Root.Children[5];
            top.Pixels.Fill(new Rgba32(255, 0, 0, 255), Path.Rectangle(100, 100, 40, 40), antialias: false);
            PixelLayer topB = (PixelLayer)b.Root.Children[5];
            topB.Pixels.Fill(new Rgba32(255, 0, 0, 255), Path.Rectangle(100, 100, 40, 40), antialias: false);
            AssertSame(Pixels(b.Render()), Pixels(a.Render()));
            a.Dispose();
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpillFileLimitIsEnforced()
    {
        string dir = IoPath.Combine(IoPath.GetTempPath(), "oneimage-spill-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var doc = new Document(256, 256, new DocumentOptions { Spill = new SpillOptions { BudgetBytes = 0, Directory = dir, MaxFileBytes = 100_000 } });
            Assert.Throws<ImageLimitException>(() =>
            {
                for (int i = 0; i < 4; i++)
                {
                    AddPixels(doc, null, "l" + i, Noise(256, 256, i), 256, 256, 0, 0).Flush();
                }
            });
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
