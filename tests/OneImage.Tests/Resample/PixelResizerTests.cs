using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.Resample;

[Collection(MemoryMeasurement.Name)]
public class PixelResizerTests
{
    private static byte[] Noise(int length, int seed)
    {
        var rnd = new Random(seed);
        var bytes = new byte[length];
        rnd.NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SteadyStateAllocatesNothing(int degree)
    {
        using var resizer = new PixelResizer(maxDegree: degree);
        (string Name, int Bpp, PixelFormat Format)[] formats = [("Gray8", 1, PixelFormat.Gray8), ("Gray16", 2, PixelFormat.Gray16), ("Rgb24", 3, PixelFormat.Rgb8), ("Rgba32", 4, PixelFormat.Rgba8)];
        int[] widths = [2300, 2287, 2311, 2298, 2340, 2255];
        foreach ((string name, int bpp, PixelFormat format) in formats)
        {
            byte[] source = Noise(2340 * 60 * bpp, 3);
            byte[] dest = new byte[1300 * 32 * bpp];
            void Call(int i)
            {
                int sw = widths[i % widths.Length];
                (int w, int h) = PixelResizer.GetSize(sw, 60, height: 32);
                resizer.ResizeRaw(source, format, sw, 60, 0, dest, w, h, 0);
            }

            for (int i = 0; i < 12; i++)
            {
                Call(i);
            }

            // With several threads the process-wide counter also sees pool threads starting up or other runtime housekeeping, so a
            // nonzero reading is measured again; the calling thread's own counter has to be exactly zero.
            long allocated = long.MaxValue;
            for (int attempt = 0; attempt < 5 && allocated != 0; attempt++)
            {
                long before = degree == 1 ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(true);
                for (int i = 0; i < 60; i++)
                {
                    Call(i);
                }

                long after = degree == 1 ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(true);
                allocated = after - before;
            }

            Assert.True(allocated == 0, $"{name} degree {degree}: {allocated} bytes over 60 calls");
        }
    }

    [Fact]
    public void TypedOverloadsAllocateNothingAndLargeSizesStayWithinTheLimit()
    {
        using var resizer = new PixelResizer(maxDegree: 1);
        var src = new Rgba32[640 * 480];
        var dst = new Rgba32[320 * 240];
        void Calls()
        {
            resizer.Resize<Rgba32>(src, 640, 480, dst, 320, 240, ResamplingFilter.Lanczos3, linear: false);
            resizer.Resize<Rgba32>(src, 640, 480, dst, 320, 240, ResamplingFilter.Mitchell, linear: true);
        }

        // A filter seen for the first time fills a tap-table slot that has never been used, which grows its buffers: that is
        // warm-up, so every variant measured below runs first.
        for (int i = 0; i < 10; i++)
        {
            Calls();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Calls();
        Calls();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.InRange(resizer.ScratchBytes, 1, 64L << 20);
    }

    [Fact]
    public void ScratchLimitIsEnforcedBeforeAllocating()
    {
        using var resizer = new PixelResizer(maxScratchBytes: 1 << 20);
        var src = new Gray8[4000 * 4000];
        var dst = new Gray8[3000 * 3000];
        Assert.Throws<ImageLimitException>(() => resizer.Resize<Gray8>(src, 4000, 4000, dst, 3000, 3000));
        Assert.True(resizer.ScratchBytes <= 1 << 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelResizer(1000));
        Assert.Throws<ImageLimitException>(() => resizer.Resize<Gray8>(src, (1 << 24) + 1, 1, dst, 3, 1));
    }

    [Fact]
    public void OutputDoesNotDependOnDegree()
    {
        foreach (string format in new[] { "Gray8", "Rgb24", "Rgba32" })
        {
            (_, byte[] bytes, PixelFormat f) = ResizeMatrix.Source(format, 1000, 700);
            byte[]? first = null;
            foreach (int degree in new[] { 1, 2, 3, 8, 32 })
            {
                using var resizer = new PixelResizer(maxDegree: degree);
                byte[] output = new byte[333 * 111 * f.BytesPerPixel];
                resizer.ResizeRaw(bytes, f, 1000, 700, 0, output, 333, 111, 0);
                first ??= output;
                Assert.True(first.AsSpan().SequenceEqual(output), $"{format} degree {degree}");
            }
        }
    }

    [Fact]
    public void ResizersOnManyThreadsAtOnceStayCorrect()
    {
        // Each thread owns a resizer that fans its bands out to the shared pool; the pool is busy with the other resizers' bands.
        (Image image, byte[] bytes, PixelFormat format) = ResizeMatrix.Source("Rgba32", 900, 600);
        byte[] expected = ResizeMatrix.Read(image.Resize(300, 200, Fit.Stretch));
        Task[] tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var resizer = new PixelResizer(maxDegree: 4);
            byte[] output = new byte[300 * 200 * 4];
            for (int i = 0; i < 40; i++)
            {
                Array.Clear(output);
                resizer.ResizeRaw(bytes, format, 900, 600, 0, output, 300, 200, 0);
                Assert.True(expected.AsSpan().SequenceEqual(output));
            }
        })).ToArray();
        Task.WaitAll(tasks);
    }

    [Fact]
    public void RejectsBadArguments()
    {
        using var resizer = new PixelResizer();
        var src = new Gray8[100];
        var dst = new Gray8[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => resizer.Resize<Gray8>(src, 0, 10, dst, 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => resizer.Resize<Gray8>(src, 10, 10, dst, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => resizer.Resize<Gray8>(src, 10, 10, dst, 5, 5, (ResamplingFilter)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => resizer.Resize<Gray8>(src, 10, 10, dst, 5, 5, sourceStride: 5));
        Assert.Throws<ArgumentException>(() => resizer.Resize<Gray8>(src, 11, 10, dst, 5, 5));
        Assert.Throws<ArgumentException>(() => resizer.Resize<Gray8>(src, 10, 10, dst, 11, 10));
        Assert.Throws<ArgumentException>(() => resizer.Resize<Gray8>(src, 10, 10, src, 5, 5));
        Assert.Throws<OperationCanceledException>(() => resizer.Resize<Gray8>(src, 10, 10, dst, 5, 5, cancellationToken: new CancellationToken(true)));
        resizer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => resizer.Resize<Gray8>(src, 10, 10, dst, 5, 5));
    }

    [Fact]
    public void OverlappingCallsAreDetected()
    {
        using var resizer = new PixelResizer(maxDegree: 1);
        var src = new Rgba32[2000 * 2000];
        var dst = new Rgba32[1000 * 1000];
        using var started = new ManualResetEventSlim();
        bool seen = false;
        var cts = new CancellationTokenSource();
        Task first = Task.Run(() =>
        {
            started.Set();
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    resizer.Resize<Rgba32>(src, 2000, 2000, dst, 1000, 1000, cancellationToken: cts.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                // Either thread can be the one that collides with the other.
                if (e is InvalidOperationException)
                {
                    Volatile.Write(ref seen, true);
                }
            }
        });
        started.Wait();
        for (int i = 0; i < 200 && !Volatile.Read(ref seen); i++)
        {
            try
            {
                resizer.Resize<Rgba32>(new Rgba32[4], 2, 2, new Rgba32[1], 1, 1);
            }
            catch (InvalidOperationException)
            {
                Volatile.Write(ref seen, true);
            }
        }

        cts.Cancel();
        first.Wait();
        Assert.True(seen);
    }

    [Fact]
    public void ReusedResizerStaysCorrectAcrossSizesAndFormats()
    {
        // Many sizes in a row on one instance (tables and windows are recycled), each compared with the lazy pipeline.
        using var resizer = new PixelResizer();
        var rnd = new Random(5);
        for (int i = 0; i < 60; i++)
        {
            string format = new[] { "Gray8", "Rgb24", "Rgba32", "Gray16" }[rnd.Next(4)];
            int sw = rnd.Next(1, 400);
            int sh = rnd.Next(1, 300);
            int dw = rnd.Next(1, 300);
            int dh = rnd.Next(1, 200);
            ResamplingFilter filter = (ResamplingFilter)rnd.Next(0, 15);
            bool linear = rnd.Next(2) == 0;
            (Image image, byte[] bytes, PixelFormat f) = ResizeMatrix.Source(format, sw, sh);
            byte[] expected = ResizeMatrix.Read(image.Resize(dw, dh, Fit.Stretch, filter: filter, linear: linear));
            byte[] got = new byte[dw * dh * f.BytesPerPixel];
            resizer.ResizeRaw(bytes, f, sw, sh, 0, got, dw, dh, 0, filter, linear);
            Assert.True(expected.AsSpan().SequenceEqual(got), $"{format} {sw}x{sh}>{dw}x{dh} {filter} linear={linear}");
        }
    }

    [Fact]
    public void ReportsHardwarePath()
    {
        string line = $"Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated} Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} Avx={System.Runtime.Intrinsics.X86.Avx.IsSupported} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Avx512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported} blockKernel={ResampleRowsKernel.IsAccelerated}";
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("ONEIMAGE_ISA_LOG") is { Length: > 0 } path)
        {
            File.WriteAllText(path, line + Environment.NewLine);
        }
    }

    // ---- kernels ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(130)]
    public void BlockedHorizontalFilterMatchesTheRowLoopBitForBit(int outPixels)
    {
        var rnd = new Random(outPixels);
        foreach (ResamplingFilter filter in new[] { ResamplingFilter.Lanczos3, ResamplingFilter.Box, ResamplingFilter.CatmullRom, ResamplingFilter.Nearest, ResamplingFilter.Lanczos8 })
        {
            foreach (int srcPixels in new[] { outPixels, (outPixels * 2) + 5, Math.Max(1, outPixels / 2) })
            {
                using var buffers = new WeightBuffers();
                buffers.Fill(srcPixels, outPixels, (double)srcPixels / outPixels, filter);
                float[] block = Enumerable.Range(0, srcPixels * 8).Select(_ => (rnd.NextSingle() * 4) - 1).ToArray();
                int stride = outPixels + 3;
                var got = new float[(7 * stride) + outPixels];
                var want = new float[got.Length];
                ResampleRowsKernel.HorizontalFilterRows(block, got, stride, buffers.Start.AsSpan(0, outPixels), buffers.Count.AsSpan(0, outPixels), buffers.Offset.AsSpan(0, outPixels), buffers.Weights);
                ResampleRowsKernel.HorizontalFilterRowsReference(block, want, stride, buffers.Start.AsSpan(0, outPixels), buffers.Count.AsSpan(0, outPixels), buffers.Offset.AsSpan(0, outPixels), buffers.Weights);
                for (int r = 0; r < 8; r++)
                {
                    // Also against the one-row kernel the lazy pipeline uses.
                    var row = new float[srcPixels];
                    for (int x = 0; x < srcPixels; x++)
                    {
                        row[x] = block[(x * 8) + r];
                    }

                    var single = new float[outPixels];
                    ResampleKernel.HorizontalFilter(row, 0, single, 1, buffers.Start.AsSpan(0, outPixels), buffers.Count.AsSpan(0, outPixels), buffers.Offset.AsSpan(0, outPixels), buffers.Weights);
                    for (int i = 0; i < outPixels; i++)
                    {
                        Assert.Equal(BitConverter.SingleToInt32Bits(single[i]), BitConverter.SingleToInt32Bits(got[(r * stride) + i]));
                        Assert.Equal(BitConverter.SingleToInt32Bits(want[(r * stride) + i]), BitConverter.SingleToInt32Bits(got[(r * stride) + i]));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(300)]
    public void Gray8BlockUnpackAndRowKernelsMatchTheGenericPaths(int width)
    {
        foreach (bool linear in new[] { true, false })
        {
            var layout = new ResampleLayout(PixelFormat.Gray8, linear);
            int stride = width + 5;
            byte[] src = Noise(stride * 8, width);
            var block = new float[width * 8];
            ResampleRowsKernel.UnpackGray8Block(src, stride, block, width, linear);
            float[] lut = linear ? SrgbTables.ToLinear8 : SrgbTables.Identity8;
            for (int r = 0; r < 8; r++)
            {
                var row = new float[width];
                ResampleKernel.UnpackRow(src.AsSpan(r * stride, width), row, width, layout);
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(lut[src[(r * stride) + x]], row[x]);
                    Assert.Equal(row[x], block[(x * 8) + r]);
                }
            }

            // Pack: random floats including out of range, negative, NaN and infinities, against the formula of the generic loop.
            var rnd = new Random(width);
            float[] values = Enumerable.Range(0, width).Select(i => i % 11 == 3 ? float.NaN : i % 13 == 5 ? float.PositiveInfinity : i % 17 == 2 ? float.NegativeInfinity : (rnd.NextSingle() * 1.6f) - 0.3f).ToArray();
            var packed = new byte[width];
            ResampleKernel.PackRow(values, packed, width, layout);
            float scale = linear ? 65535f : 255f;
            int max = linear ? 65535 : 255;
            for (int x = 0; x < width; x++)
            {
                int code = Math.Clamp((int)((values[x] * scale) + 0.5f), 0, max);
                Assert.Equal(linear ? SrgbTables.FromLinear8[code] : (byte)code, packed[x]);
            }
        }
    }
}
