using System.Diagnostics;
using System.Runtime;

namespace OneImage.Tests;

/// <summary>
/// Tests that assert on GC heap size or allocated bytes. Those counters are process-wide, so allocations made by
/// tests running at the same time would be counted against the test being measured; this collection runs alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurement
{
    /// <summary>The collection name to put on measuring test classes.</summary>
    public const string Name = "Memory measurement";
}

/// <summary>
/// Reads the managed heap for the streaming-memory tests in a way that does not depend on when the runtime happens to collect.
/// <see cref="GC.GetTotalMemory(bool)"/> without a collection also counts garbage that has not been collected yet, and how much
/// of that piles up depends on the runtime's adaptive allocation budget, which grows with the allocation history of the whole
/// test process (large caches on the host make it grow a lot). So a reading that exceeds the bound is re-taken after a full
/// collection: data that is really held (the thing the bound is about) still counts, garbage does not.
/// </summary>
internal static class LiveHeap
{
    /// <summary>
    /// Managed heap size without the idle buffers of the tile and float pools. The pools keep up to 48 + 32 MiB for reuse, and how
    /// full they are when a test starts depends on which tests ran before it (on a 3-core macOS runner the PNG pipeline test
    /// saw 58 MiB in them at the peak against a nearly empty pool at its baseline). That memory is bounded by the pool caps
    /// and is not what the streaming bounds are about, so it is left out of both the baseline and the readings.
    /// </summary>
    /// <param name="collect">Whether to collect first.</param>
    /// <returns>Heap bytes net of idle pool buffers.</returns>
    public static long Total(bool collect) => (collect ? Survivors() : GC.GetTotalMemory(false)) - PooledTileStore.Instance.PooledBytes - FloatScratch.PooledBytes;

    /// <summary>
    /// Bytes that survive a full blocking collection, as the collector counted them while marking. <see cref="GC.GetTotalMemory(bool)"/>
    /// is not used for this because it adds bytes that are not live data: on an Apple-silicon runner it read about 40 MiB above
    /// the surviving bytes of the same collection (103 against 64 MiB of heap, of which the process had allocated 80 MiB in total),
    /// which made a streaming path holding 50 MiB look like it held 98.
    /// </summary>
    /// <returns>Surviving bytes.</returns>
    public static long Survivors()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        return GC.GetGCMemoryInfo(GCKind.FullBlocking).PromotedBytes;
    }

    /// <summary>Takes the baseline reading of a test and starts the growth profile.</summary>
    /// <returns>Heap bytes net of idle pool buffers after a full collection.</returns>
    public static long Baseline()
    {
        s_samples.Clear();
        s_clock = Stopwatch.StartNew();
        BaselineBytes = Total(true);
        return BaselineBytes;
    }

    /// <summary>Gets the heap size taken as the baseline (diagnostics).</summary>
    public static long BaselineBytes { get; private set; }

    private static readonly List<(long Ms, long Growth)> s_samples = [];
    private static Stopwatch s_clock = Stopwatch.StartNew();

    /// <summary>
    /// Describes how the sampled growth moved over the run: the peak in each fifth of the elapsed time. A streaming path is flat
    /// (every fifth peaks at about the same size); a leak or an accumulating cache climbs from fifth to fifth.
    /// </summary>
    /// <returns>One line of MiB figures.</returns>
    public static string Flatness()
    {
        (long Ms, long Growth)[] all = s_samples.ToArray();
        if (all.Length < 5)
        {
            return "growth by fifth of the run: too few samples";
        }

        long end = all[^1].Ms + 1;
        var peaks = new long[5];
        foreach ((long ms, long g) in all)
        {
            int bin = (int)(ms * 5 / end);
            peaks[bin] = Math.Max(peaks[bin], g);
        }

        return "peak growth by fifth of the run: " + string.Join(" / ", peaks.Select(p => (p >> 20) + " MiB"));
    }

    /// <summary>Gets the last reading that needed a collection, as growth before and after it (diagnostics).</summary>
    public static (long Before, long After) LastForced { get; private set; }

    /// <summary>Returns the heap size, taken from a full collection when it exceeds <paramref name="baseline"/> plus half of <paramref name="limit"/>.</summary>
    /// <param name="baseline">Heap size after a full collection before the measured work.</param>
    /// <param name="limit">The growth bound of the test.</param>
    /// <returns>A heap size that includes everything still reachable and, below half the bound, some garbage.</returns>
    public static long Read(long baseline, long limit)
    {
        long size = Total(false);
        if (size - baseline <= limit / 2)
        {
            s_samples.Add((s_clock.ElapsedMilliseconds, size - baseline));
            return size;
        }

        long live = Total(true);
        LastForced = (size - baseline, live - baseline);
        s_samples.Add((s_clock.ElapsedMilliseconds, live - baseline));
        return live;
    }
}

/// <summary>Describes where the managed heap is at a moment, for the failure messages of the memory tests.</summary>
internal static class HeapReport
{
    /// <summary>Returns a one-line breakdown: heap and generation sizes, pool and cache contents, live tiles, platform and degree.</summary>
    public static string Describe()
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo(GCKind.Any);
        var gens = new System.Text.StringBuilder();
        for (int i = 0; i < gc.GenerationInfo.Length; i++)
        {
            gens.Append(i == 0 ? "" : "/").Append(gc.GenerationInfo[i].SizeAfterBytes >> 20);
        }

        long live = Tile.RentedCount - Tile.ReturnedCount;
        (long before, long after) = LiveHeap.LastForced;
        return $"baseline {LiveHeap.BaselineBytes >> 10} KiB, now {GC.GetTotalMemory(false) >> 10} KiB, committed {gc.TotalCommittedBytes >> 20} MiB, last forced collection: growth {before >> 20} -> {after >> 20} MiB; heap {gc.HeapSizeBytes >> 20} MiB (gen0/1/2/loh/poh after GC: {gens} MiB, fragmented {gc.FragmentedBytes >> 20}, pinned {gc.PinnedObjectsCount}), " +
            $"tile pool {PooledTileStore.Instance.PooledBytes >> 20} MiB, float pool {FloatScratch.PooledBytes >> 20} MiB, GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, allocated {GC.GetTotalAllocatedBytes() >> 20} MiB, cache {TileCache.Global.Bytes >> 20}+{TileCache.Streaming.Bytes >> 20} MiB, live tiles {live}, " +
            $"decode threads {StreamingSession.LiveThreads}, cpus {Environment.ProcessorCount}, degree {ImageEngine.DefaultCallDegree}, " +
            $"gc {(GCSettings.IsServerGC ? "server" : "workstation")}/{GCSettings.LatencyMode}, os {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}";
    }
}

