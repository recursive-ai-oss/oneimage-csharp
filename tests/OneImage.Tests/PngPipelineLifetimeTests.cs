using System.Collections.Concurrent;
using OneImage.Tests.Png;

namespace OneImage.Tests;

/// <summary>
/// The pipelined PNG decode inflates the next batch of rows on a pool thread while this thread unfilters the current one. Whatever
/// ends the decode early (a failing sink, a canceled token, a read that throws, damaged rows), no inflate may still be reading
/// the source or writing a pooled buffer once the call has left. Each case runs against poisoning pools, which report a write
/// to a buffer after it went back, and against a stream that reports any read that is in flight or starts after the decode left.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class PngPipelineLifetimeTests
{
    private const int W = 1100;
    private const int H = 1100;

    private sealed class TrackingStream(byte[] data, int throwOnRead = 0) : Stream
    {
        private readonly Random _nap = new(3);
        private int _position;
        private int _reads;
        private int _inFlight;
        private volatile bool _left;
        private volatile bool _armed;

        /// <summary>Gets reads that were running or began after the decode returned.</summary>
        public int LateReads { get; private set; }

        public int Reads => Volatile.Read(ref _reads);

        public void Arm()
        {
            Volatile.Write(ref _reads, 0);
            _armed = true;
        }

        public void DecodeLeft()
        {
            _left = true;
            if (Volatile.Read(ref _inFlight) != 0)
            {
                LateReads++;
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)Math.Clamp(value, 0, data.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_left)
            {
                LateReads++;
            }

            Interlocked.Increment(ref _inFlight);
            try
            {
                int n = Interlocked.Increment(ref _reads);
                if (_armed && throwOnRead > 0 && n == throwOnRead)
                {
                    throw new IOException("injected read failure");
                }

                // Make the reading thread slower or faster than the one unfiltering, at random.
                int nap;
                lock (_nap)
                {
                    nap = _nap.Next(4) == 0 ? _nap.Next(3) : -1;
                }

                if (nap >= 0)
                {
                    Thread.Sleep(nap);
                }

                int take = Math.Min(Math.Min(buffer.Length, data.Length - _position), 40_000);
                data.AsSpan(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => data.Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HookSink(FrameInfo frame, Action<int> onWrite) : IRowSink
    {
        private int _calls;

        public int Width => frame.Width;

        public int Height => frame.Height;

        public PixelFormat Format => frame.Format;

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount) => onWrite(_calls++);
    }

    private static byte[] GrayPng(int damagedRow = -1)
    {
        if (damagedRow < 0)
        {
            return PngTestUtil.Encode(PngGen.Photo(W, H, PixelFormat.Gray8, 11), W, H, PixelFormat.Gray8);
        }

        // Valid deflate data whose row `damagedRow` starts with an impossible filter byte.
        byte[] raw = new byte[H * (W + 1)];
        new Random(2).NextBytes(raw);
        for (int y = 0; y < H; y++)
        {
            raw[y * (W + 1)] = (byte)(y % 5);
        }

        raw[damagedRow * (W + 1)] = 255;
        return MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(W, H, 8, 0)), MiniPng.Chunk("IDAT", MiniPng.Zlib(raw)), MiniPng.Chunk("IEND", []));
    }

    private enum Fault
    {
        SinkThrows,
        CancelInsideSink,
        ReadThrows,
        DamagedRow,
        None,
    }

    private static string Run(byte[] file, Fault fault, int at, int seed)
    {
        using var cts = new CancellationTokenSource();
        var src = new TrackingStream(file, fault == Fault.ReadThrows ? 3 + at : 0);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(src, ImageLimits.Default);
        src.Position = 0;
        src.Arm();
        var sink = new HookSink(info.Frames[0], call =>
        {
            if (call == at && fault == Fault.SinkThrows)
            {
                throw new InvalidOperationException("injected sink failure");
            }

            if (call == at && fault == Fault.CancelInsideSink)
            {
                cts.Cancel();
            }
        });

        string outcome;
        try
        {
            PngFormat.Instance.Decoder.DecodeFrame(src, info, 0, new DecodeRequest(new ImageRect(0, 0, W, H)), sink, ImageLimits.Default, cts.Token);
            outcome = "ok";
        }
        catch (Exception e)
        {
            outcome = e.GetType().Name;
        }
        finally
        {
            src.DecodeLeft();
        }

        // Anything still running on a pool thread would show up now, as a late read or as a poisoned buffer.
        Thread.Sleep(15);
        Assert.True(src.LateReads == 0, $"{fault} at {at} (seed {seed}): the source was read after the decode returned or while it returned");
        return outcome;
    }

    [Fact]
    public void NothingKeepsInflatingAfterAPipelinedDecodeEndsEarly()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_POOLCHECK_ROUNDS"), out int r) ? Math.Max(r / 10, 4) : 4;
        byte[] good = GrayPng();
        byte[][] damaged = [GrayPng(150), GrayPng(480), GrayPng(1000)];
        var outcomes = new ConcurrentDictionary<string, int>();

        string[] violations;
        using (var check = new PoolCheck())
        {
            // Many decodes at once keep the pool busy, so some batches are still queued when a decode ends.
            Parallel.For(0, 12, new ParallelOptions { MaxDegreeOfParallelism = 12 }, worker =>
            {
                for (int n = 0; n < rounds; n++)
                {
                    for (int at = 0; at < 11; at++)
                    {
                        string a = Run(good, Fault.SinkThrows, at, worker);
                        string b = Run(good, Fault.CancelInsideSink, at, worker);
                        string c = Run(good, Fault.ReadThrows, at * 3, worker);
                        outcomes.AddOrUpdate("sink:" + a, 1, (_, v) => v + 1);
                        outcomes.AddOrUpdate("cancel:" + b, 1, (_, v) => v + 1);
                        outcomes.AddOrUpdate("read:" + c, 1, (_, v) => v + 1);
                    }

                    foreach (byte[] d in damaged)
                    {
                        outcomes.AddOrUpdate("damaged:" + Run(d, Fault.DamagedRow, 0, worker), 1, (_, v) => v + 1);
                    }

                    Assert.Equal("ok", Run(good, Fault.None, 0, worker));
                }
            });
            violations = check.Finish();
        }

        Assert.True(violations.Length == 0, string.Join("\n=====\n", violations));

        // Each fault ends with the exception that was injected (never a different type from the thread that was still working).
        foreach ((string key, int count) in outcomes)
        {
            Assert.True(
                key is "sink:InvalidOperationException" or "sink:ok"
                    or "cancel:OperationCanceledException" or "cancel:TaskCanceledException" or "cancel:ok"
                    or "read:IOException" or "read:ok"
                    or "damaged:ImageFormatException",
                $"unexpected outcome {key} ({count} times)");
        }

        Assert.True(outcomes.ContainsKey("sink:InvalidOperationException") && outcomes.ContainsKey("read:IOException") && outcomes.ContainsKey("damaged:ImageFormatException"), string.Join(", ", outcomes.Select(o => o.Key)));
    }
}
