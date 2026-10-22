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
    /// <summary>Returns the heap size, collected first when it exceeds <paramref name="baseline"/> plus <paramref name="limit"/>.</summary>
    /// <param name="baseline">Heap size after a full collection before the measured work.</param>
    /// <param name="limit">The growth bound of the test.</param>
    /// <returns>A heap size that includes everything still reachable and, below the bound, some garbage.</returns>
    public static long Read(long baseline, long limit)
    {
        long size = GC.GetTotalMemory(false);
        return size - baseline > limit ? GC.GetTotalMemory(true) : size;
    }
}
