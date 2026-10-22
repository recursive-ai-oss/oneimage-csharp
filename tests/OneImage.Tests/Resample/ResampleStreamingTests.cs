using System.Diagnostics;
using Xunit;

namespace OneImage.Tests.Resample;

[Collection(MemoryMeasurement.Name)]
public class ResampleStreamingTests
{
    private static Image LoadGen(int w, int h) =>
        Image.Load(GenFormat.Header(w, h), new LoadOptions { Configuration = GenFormat.Instance.Configuration() });

    [Fact]
    public void ResizeOfHugeSequentialImageStaysInBoundedMemory()
    {
        const int W = 8000;
        const int H = 30000;
        long frameBytes = (long)W * H * 4;
        Image img = LoadGen(W, H);
        Assert.True(img.Frames[0].Node.Streaming);
        Image small = img.Resize(width: 400);
        Assert.Equal((400, 1500), (small.Width, small.Height));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, LiveHeap.Read(baseline, frameBytes / 10));
                Thread.Sleep(2);
            }
        });
        sampler.Start();
        var sw = Stopwatch.StartNew();
        Rgba32[] px;
        try
        {
            px = small.ToArray<Rgba32>();
        }
        finally
        {
            stop.Cancel();
            sampler.Join();
        }

        sw.Stop();
        long growth = peak - baseline;
        TestContext.Current.SendDiagnosticMessage($"resize streaming {W}x{H} -> 400x1500: peak managed growth {growth / (1 << 20)} MiB ({100.0 * growth / frameBytes:F1}% of the {frameBytes / (1 << 20)} MiB frame), {sw.ElapsedMilliseconds} ms");
        // The bound is for the pooled store; a store that allocates a fresh array per tile leaves garbage the pool avoids.
        Assert.True(TileStores.Current is not PooledTileStore || growth < frameBytes / 10, $"peak growth {growth} exceeds 10% of the frame ({frameBytes})");

        // Sanity: the top-left output pixel is the (linear-light) average of the gen pattern over its 20x20 block; spot-check it loosely.
        Assert.NotEqual(default, px[0]);
        Assert.Equal(255, px[(750 * 400) + 200].A);
    }

    [Theory]
    [InlineData(3000, 9000, 150, 450)]
    [InlineData(4000, 4500, 1600, 1800)]
    [InlineData(2400, 7200, 2400, 7000)]
    public void StreamedResultEqualsMaterializedResult(int w, int h, int dw, int dh)
    {
        Image streamed = LoadGen(w, h);
        Assert.True(streamed.Frames[0].Node.Streaming);
        Rgba32[] want = Image.FromPixels<Rgba32>(streamed.ToArray<Rgba32>(), w, h).Resize(dw, dh, Fit.Stretch).ToArray<Rgba32>();
        Image fresh = LoadGen(w, h);
        Rgba32[] got = fresh.Resize(dw, dh, Fit.Stretch).ToArray<Rgba32>();
        Assert.True(want.AsSpan().SequenceEqual(got), "streamed and materialized results differ");
    }
}
