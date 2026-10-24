using System.Buffers;
using System.Runtime.CompilerServices;

namespace OneImage.Tests;

/// <summary>
/// A stream opened on a loaded image's compressed bytes must keep those bytes valid while it is in use, even when the image that
/// produced it is gone: the bytes live in a pooled buffer that is given back when the image is collected.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class PooledSourceLifetimeTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Stream[] OpenAndDropOwners(byte[] data, int count)
    {
        var streams = new Stream[count];
        for (int i = 0; i < count; i++)
        {
            using var input = new MemoryStream(data);
            PooledBytes bytes = PooledBytes.CopyFrom(input);
            var source = new MemoryImageSource(bytes.Memory, bytes);
            streams[i] = source.Open();
        }

        return streams;
    }

    [Fact]
    public void BytesStayIntactWhileAStreamOpenedOnThemIsReachable()
    {
        byte[] data = new byte[100_000];
        new Random(5).NextBytes(data);
        Stream[] streams = OpenAndDropOwners(data, 4);

        // Everything but the streams is garbage now. Collect, let finalizers run, and let another user of the pool take and write
        // buffers of the same size, as a decode on another thread does.
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var taken = new List<byte[]>();
        for (int i = 0; i < 12; i++)
        {
            byte[] b = ArrayPool<byte>.Shared.Rent(data.Length + 1);
            b.AsSpan().Fill(0xA5);
            taken.Add(b);
        }

        foreach (Stream s in streams)
        {
            byte[] read = new byte[data.Length];
            s.ReadExactly(read);
            Assert.True(data.AsSpan().SequenceEqual(read), "the stream's bytes were overwritten after their owner was collected");
        }

        foreach (byte[] b in taken)
        {
            ArrayPool<byte>.Shared.Return(b);
        }

        GC.KeepAlive(streams);
    }
}
