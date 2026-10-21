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
