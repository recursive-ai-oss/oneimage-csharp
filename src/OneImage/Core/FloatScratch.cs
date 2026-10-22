namespace OneImage;

/// <summary>
/// Process-wide pool of power-of-two float scratch arrays, one queue per size. The shared <see cref="System.Buffers.ArrayPool{T}"/> keeps
/// its arrays in per-core buckets, so when worker threads move between cores a rental often misses the bucket it was returned to
/// and allocates a new array; with scratch of several hundred KB per worker that shows up as resident memory that keeps growing
/// across calls. A single queue per size hands the same few arrays back out. Contents of a rented array are unspecified.
/// </summary>
internal static class FloatScratch
{
    private const int MinShift = 10;
    private const int MaxShift = 22;
    private const long MaxPooledBytes = 32L << 20;

    private static readonly System.Collections.Concurrent.ConcurrentQueue<float[]>[] s_queues = CreateQueues();
    private static long s_pooledBytes;

    public static float[] Rent(int length)
    {
        int shift = Shift(length);
        if (shift > MaxShift)
        {
            return GC.AllocateUninitializedArray<float>(length);
        }

        if (s_queues[shift - MinShift].TryDequeue(out float[]? pooled))
        {
            Interlocked.Add(ref s_pooledBytes, -(long)pooled.Length * sizeof(float));
            return pooled;
        }

        return GC.AllocateUninitializedArray<float>(1 << shift);
    }

    public static void Return(float[]? array)
    {
        if (array == null)
        {
            return;
        }

        int shift = Shift(array.Length);
        if (shift > MaxShift || array.Length != 1 << shift)
        {
            return;
        }

        long bytes = (long)array.Length * sizeof(float);
        if (Interlocked.Add(ref s_pooledBytes, bytes) > MaxPooledBytes)
        {
            Interlocked.Add(ref s_pooledBytes, -bytes);
            return;
        }

        s_queues[shift - MinShift].Enqueue(array);
    }

    private static int Shift(int length) => Math.Max(MinShift, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(1, length) - 1));

    private static System.Collections.Concurrent.ConcurrentQueue<float[]>[] CreateQueues()
    {
        var q = new System.Collections.Concurrent.ConcurrentQueue<float[]>[MaxShift - MinShift + 1];
        for (int i = 0; i < q.Length; i++)
        {
            q[i] = new();
        }

        return q;
    }
}
