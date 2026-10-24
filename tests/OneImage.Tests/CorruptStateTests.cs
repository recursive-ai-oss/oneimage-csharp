using System.Collections.Concurrent;
using OneImage.Tests.Png;

namespace OneImage.Tests;

/// <summary>
/// Corrupt data must surface as <see cref="ImageFormatException"/> (or a limit exception), never as an index or argument exception
/// from inside a decoder. Two sources of corruption are tried: damaged files, and arrays the decoder rented that something else
/// overwrites while it works (the effect of a caller that writes to an array after returning it to the shared pool).
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class CorruptStateTests
{
    /// <summary>A pool that hands out ordinary arrays and keeps overwriting the ones that are still rented.</summary>
    private sealed class ScribblingPool : System.Buffers.ArrayPool<byte>, IDisposable
    {
        private readonly System.Buffers.ArrayPool<byte> _inner = System.Buffers.ArrayPool<byte>.Shared;
        private readonly ConcurrentDictionary<byte[], byte> _out = new(ReferenceEqualityComparer.Instance);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private readonly byte _value;

        public ScribblingPool(byte value, int seed)
        {
            _value = value;
            _worker = Task.Run(() =>
            {
                var rnd = new Random(seed);
                while (!_stop.IsCancellationRequested)
                {
                    byte[][] live = [.. _out.Keys];
                    if (live.Length > 0)
                    {
                        byte[] a = live[rnd.Next(live.Length)];
                        int len = Math.Min(a.Length, 1 + rnd.Next(4096));
                        int at = rnd.Next(Math.Max(1, a.Length - len));
                        try
                        {
                            a.AsSpan(at, len).Fill(_value == 0 ? (byte)rnd.Next(256) : _value);
                        }
                        catch (ArgumentException)
                        {
                        }
                    }

                    Thread.SpinWait(rnd.Next(2000));
                }
            });
        }

        public override byte[] Rent(int minimumLength)
        {
            byte[] a = _inner.Rent(minimumLength);
            _out[a] = 0;
            return a;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            _out.TryRemove(array, out _);
            _inner.Return(array, clearArray);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _worker.Wait();
        }
    }

    private static string Describe(Exception e)
    {
        string top = e.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return $"{e.GetType().Name} at {top}";
    }

    private static bool Typed(Exception e) => e is ImageException or OperationCanceledException;

    private static byte[] PipelinedGray()
    {
        const int W = 1100;
        return PngTestUtil.Encode(PngGen.Photo(W, W, PixelFormat.Gray8, 5), W, W, PixelFormat.Gray8);
    }

    [Fact]
    public void DamagedPngDataOnlyEverFailsWithFormatErrors()
    {
        byte[] good = PipelinedGray();
        var idat = PngGen.Chunks(good).Where(c => c.Name == "IDAT").ToList();
        var rnd = new Random(77);
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_POOLCHECK_ROUNDS"), out int r) ? r : 60;
        var untyped = new List<string>();
        for (int n = 0; n < rounds; n++)
        {
            byte[] bad = (byte[])good.Clone();
            var c = idat[rnd.Next(idat.Count)];
            int len = rnd.Next(3) switch { 0 => 1 + rnd.Next(8), 1 => 1 + rnd.Next(300), _ => 1 + rnd.Next(40_000) };
            int at = c.Data + rnd.Next(Math.Max(1, c.Length - Math.Min(len, c.Length)));
            len = Math.Min(len, c.Data + c.Length - at);
            switch (rnd.Next(3))
            {
                case 0:
                    bad.AsSpan(at, len).Fill(0xFF);
                    break;
                case 1:
                    bad.AsSpan(at, len).Clear();
                    break;
                default:
                    rnd.NextBytes(bad.AsSpan(at, len));
                    break;
            }

            try
            {
                Image.Load(bad).ToArray<Gray8>();
            }
            catch (Exception e) when (!Typed(e))
            {
                untyped.Add(Describe(e));
            }
            catch (Exception)
            {
            }
        }

        Assert.True(untyped.Count == 0, string.Join("\n", untyped.Distinct().Take(10)));
    }

    [Theory]
    [InlineData((byte)0xFF)]
    [InlineData((byte)0)]
    public void ArraysOverwrittenWhileInUseOnlyEverFailWithFormatErrors(byte value)
    {
        byte[] file = PipelinedGray();
        string path = Path.Combine(Path.GetTempPath(), $"oneimage-corrupt-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, file);
        System.Buffers.ArrayPool<byte> previous = Pools<byte>.Pool;
        var kinds = new ConcurrentDictionary<string, int>();
        var untyped = new ConcurrentQueue<string>();
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_POOLCHECK_ROUNDS"), out int r) ? Math.Max(r / 5, 20) : 20;
        try
        {
            using var pool = new ScribblingPool(value, 5);
            Pools<byte>.Pool = pool;
            Parallel.For(0, 4, _ =>
            {
                for (int n = 0; n < rounds; n++)
                {
                    try
                    {
                        Image img = Image.Load(path);
                        Gray8[] g = new Gray8[img.Width * img.Height];
                        img.CopyPixelsTo<Gray8>(g, new ImageRect(0, 0, img.Width, img.Height), false, 4);
                        kinds.AddOrUpdate("ok", 1, (_, v) => v + 1);
                    }
                    catch (Exception e)
                    {
                        string d = Describe(e);
                        kinds.AddOrUpdate(d, 1, (_, v) => v + 1);
                        if (!Typed(e))
                        {
                            untyped.Enqueue(d + " :: " + e.Message);
                        }
                    }
                }
            });
        }
        finally
        {
            Pools<byte>.Pool = previous;
            File.Delete(path);
        }

        Assert.True(untyped.IsEmpty, string.Join("\n", untyped.Distinct().Take(10)) + "\nall: " + string.Join("; ", kinds.Select(k => $"{k.Value}x {k.Key}")));
    }
}
