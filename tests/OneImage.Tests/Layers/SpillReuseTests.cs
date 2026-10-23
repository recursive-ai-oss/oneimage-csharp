using IoPath = System.IO.Path;

namespace OneImage.Tests.Layers;

public class SpillReuseTests
{
    [Fact]
    public void EvictedMemoryWaitsForTheReadersThatCouldSeeIt()
    {
        // 2 MiB is above the epoch quantum, so one deferred buffer closes its epoch; the size is one no tile uses, so nothing else touches it.
        const int Size = 1 << 21;
        var buffer = new byte[Size];
        SpillEpochs.Scope scope = SpillEpochs.Enter();
        SpillEpochs.Defer(buffer);
        Assert.Null(SpillEpochs.Take(Size));
        scope.Dispose();

        byte[]? back = null;
        for (int i = 0; i < 500 && back == null; i++)
        {
            back = SpillEpochs.Take(Size);
            if (back == null)
            {
                Thread.Sleep(10);
            }
        }

        Assert.Same(buffer, back);
    }

    [Fact]
    public void UnannouncedReadersKeepTheirBufferFromBeingReused()
    {
        string dir = IoPath.Combine(IoPath.GetTempPath(), "oneimage-spill-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SpillTileStore(0, dir, long.MaxValue);
            Tile t = Tile.Rent(Tile.ByteSize(PixelFormat.Rgba8), clear: false, store);
            t.Span.Fill(7);
            t.Seal();
            store.EnforceBudget();

            // The read is outside any scope, so the buffer it got must not come back as someone else's tile.
            ReadOnlySpan<byte> span = t.Span;
            byte[]? other = null;
            Tile other1 = Tile.Rent(Tile.ByteSize(PixelFormat.Rgba8), clear: false, store);
            other1.Span.Fill(9);
            other1.Seal();
            store.EnforceBudget();
            other = other1.Span.ToArray();
            Assert.Equal(7, span[0]);
            Assert.Equal(7, span[span.Length - 1]);
            Assert.Equal(9, other[0]);
            t.Release();
            other1.Release();
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
    public void ScopedReadersNeverSeeReusedMemoryUnderChurn()
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

            int bad = 0;
            for (int round = 0; round < 4; round++)
            {
                Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
                {
                    var rng = new Random((round * 100) + worker);
                    for (int n = 0; n < 400; n++)
                    {
                        int i = rng.Next(tiles.Length);
                        Tile t = tiles[i].Retain();
                        using (SpillEpochs.Scope scope = SpillEpochs.Enter())
                        {
                            ReadOnlySpan<byte> span = t.Span;
                            byte want = (byte)(i + 1);

                            // Linger, touching other tiles on this thread, so evictions and reuse happen while the span is held.
                            Tile u = tiles[rng.Next(tiles.Length)].Retain();
                            _ = u.Span[0];
                            u.Release();
                            Thread.SpinWait(rng.Next(200));
                            if (span[0] != want || span[span.Length / 2] != want || span[span.Length - 1] != want)
                            {
                                Interlocked.Increment(ref bad);
                            }
                        }

                        t.Release();
                    }
                });
            }

            Assert.Equal(0, bad);
            Assert.True(store.Statistics.TilesRead > 0);
            foreach (Tile t in tiles)
            {
                t.Release();
            }
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
    public void ScopedReadsLetTheStoreRecycleMemoryInsteadOfLeavingItToTheCollector()
    {
        string dir = IoPath.Combine(IoPath.GetTempPath(), "oneimage-spill-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SpillTileStore(512 * 1024, dir, long.MaxValue);
            var tiles = new Tile[256];
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i] = Tile.Rent(Tile.ByteSize(PixelFormat.Rgba8), clear: false, store);
                tiles[i].Span.Fill((byte)i);
                tiles[i].Seal();
            }

            // 16 MiB of tiles read one at a time through a 512 KiB budget: about 30 MiB of page-ins.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Tile t in tiles)
                {
                    using SpillEpochs.Scope scope = SpillEpochs.Enter();
                    Assert.Equal((byte)Array.IndexOf(tiles, t), t.Span[100]);
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(store.Statistics.TilesRead >= 256);
            Assert.True(allocated < 12L << 20, $"allocated {allocated >> 20} MiB while paging in {store.Statistics.TilesRead} tiles");
            foreach (Tile t in tiles)
            {
                t.Release();
            }
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
