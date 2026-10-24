using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OneImage.Tests.Png;

namespace OneImage.Tests;

/// <summary>
/// Runs the library's public surface on several threads against pools that poison what is returned (see <see cref="PoolCheck"/>),
/// with cancellations and read failures injected, and compares every result with a run made before the pools were swapped.
/// A write after a return, a double return, or any result that differs means the library misuses a rented array.
/// The default run takes a few seconds; set <c>ONEIMAGE_POOLCHECK_ROUNDS</c> for a longer one.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class PoolCheckTests : IDisposable
{
    private static readonly AsyncLocal<int[]?> s_readsUntilFault = new();
    private readonly List<string> _files = [];

    public void Dispose()
    {
        HandleStream.TestReadAt = null;
        foreach (string f in _files)
        {
            File.Delete(f);
        }
    }

    private sealed record Input(string Name, string Path, byte[] Bytes);

    private sealed record Op(string Name, Func<Input, CancellationToken, byte[]> Run);

    private Input Make(string name, Image image, IImageFormat format, ImageEncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, new SaveOptions { EncoderOptions = options });
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"oneimage-poolcheck-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, ms.ToArray());
        _files.Add(path);
        return new Input(name, path, ms.ToArray());
    }

    private List<Input> Inputs()
    {
        static Image Photo(int w, int h, PixelFormat f) => PngTestUtil.FromRaw(PngGen.Photo(w, h, f, 7), w, h, f);
        var list = new List<Input>
        {
            // Over 1 MiB of scanlines: the pipelined inflate. Gray8 is the layout of a typical scan or page image.
            Make("png-gray-pipelined", Photo(1100, 1100, PixelFormat.Gray8), PngFormat.Instance),
            Make("png-rgb-pipelined", Photo(700, 640, PixelFormat.Rgb8), PngFormat.Instance),
            Make("png-rgba-small", Photo(210, 150, PixelFormat.Rgba8), PngFormat.Instance),
            Make("jpeg", Photo(640, 480, PixelFormat.Rgb8), JpegFormat.Instance),
            Make("jpeg-progressive", Photo(640, 480, PixelFormat.Rgb8), JpegFormat.Instance, new JpegEncoderOptions { Progressive = true }),
            Make("tiff", Photo(500, 400, PixelFormat.Rgb8), TiffFormat.Instance),
            Make("gif", Photo(300, 200, PixelFormat.Rgba8), GifFormat.Instance),
            Make("webp-lossy", Photo(400, 300, PixelFormat.Rgba8), WebPFormat.Instance),
            Make("webp-lossless", Photo(400, 300, PixelFormat.Rgba8), WebPFormat.Instance, new WebPEncoderOptions { Lossless = true }),
            Make("bmp", Photo(300, 200, PixelFormat.Rgb8), BmpFormat.Instance),
        };

        // An interlaced PNG, written by hand.
        byte[] adam7 = ShortReadFixtures.InterlacedPng(260, 190, 3000);
        string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"oneimage-poolcheck-{Guid.NewGuid():N}");
        File.WriteAllBytes(p, adam7);
        _files.Add(p);
        list.Add(new Input("png-adam7", p, adam7));
        return list;
    }

    private static byte[] Digest(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    private static byte[] Digest<T>(T[] pixels)
        where T : unmanaged => Digest(System.Runtime.InteropServices.MemoryMarshal.AsBytes<T>(pixels));

    private static byte[] Encode(Image img, IImageFormat format, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        img.Save(ms, format, null, ct);
        return Digest(ms.ToArray());
    }

    private static List<Op> Ops() =>
    [
        new("whole-gray-bypass", (i, ct) =>
        {
            Image img = Image.Load(i.Path, null, ct);
            Gray8[] g = new Gray8[img.Width * img.Height];
            img.CopyPixelsTo<Gray8>(g, new ImageRect(0, 0, img.Width, img.Height), false, 4, ct);
            return Digest(g);
        }),
        new("stream-gray-bypass", (i, ct) =>
        {
            using var ms = new MemoryStream(i.Bytes);
            Image img = Image.Load(ms, null, ct);
            Gray8[] g = new Gray8[img.Width * img.Height];
            img.CopyPixelsTo<Gray8>(g, new ImageRect(0, 0, img.Width, img.Height), false, 4, ct);
            return Digest(g);
        }),
        new("whole-rgba", (i, ct) => Digest(Image.Load(i.Path, null, ct).ToArray<Rgba32>(4, ct))),
        new("stream-whole", (i, ct) => Digest(Image.Load(new ShortReadStream(i.Bytes, i.Bytes.Length, 5000), null, ct).ToArray<Rgba32>(2, ct))),
        new("regions", (i, ct) =>
        {
            Image img = Image.Load(i.Path, null, ct);
            int w = img.Width;
            int h = img.Height;
            var rect = new ImageRect(w / 5, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 3));
            using var all = new MemoryStream();
            foreach (bool cache in (ReadOnlySpan<bool>)[true, false])
            {
                Gray8[] g = new Gray8[rect.Width * rect.Height];
                img.CopyPixelsTo<Gray8>(g, rect, cache, 3, ct);
                all.Write(Digest(g));
            }

            img.ReleaseDecodedPixels();
            Rgba32[] c = new Rgba32[rect.Width * rect.Height];
            img.CopyPixelsTo<Rgba32>(c, rect, 3, ct);
            all.Write(Digest(c));
            return Digest(all.ToArray());
        }),
        new("gray-convert", (i, ct) => Digest(Image.Load(i.Path, null, ct).ConvertTo(PixelFormat.Gray8).ToArray<Gray8>(3, ct))),
        new("resize-rotate-crop-png", (i, ct) =>
        {
            Image img = Image.Load(i.Path, null, ct);
            Image r = img.Resize(width: Math.Max(8, img.Width / 2)).Rotate(17).Crop(3, 4, 40, 30);
            return Encode(r, PngFormat.Instance, ct);
        }),
        new("resize-jpeg", (i, ct) => Encode(Image.Load(i.Path, null, ct).Resize(width: 150), JpegFormat.Instance, ct)),
        new("rotate-webp", (i, ct) => Encode(Image.Load(i.Path, null, ct).Rotate(33).Crop(5, 5, 90, 70), WebPFormat.Instance, ct)),
        new("frompixels-resize", (i, ct) =>
        {
            Image src = Image.Load(i.Path, null, ct);
            Rgba32[] px = src.ToArray<Rgba32>(2, ct);
            return Digest(Image.FromPixels<Rgba32>(px, src.Width, src.Height).Resize(width: 97).ToArray<Rgba32>(2, ct));
        }),
        new("thumbnail-png", (i, ct) => Encode(Image.Load(i.Path, null, ct).Resize(width: 64, height: 64, fit: Fit.Cover), PngFormat.Instance, ct)),
    ];

    private static byte[]? TryRun(Op op, Input input, CancellationToken ct, out Exception? error)
    {
        try
        {
            error = null;
            return op.Run(input, ct);
        }
        catch (Exception e)
        {
            error = e;
            return null;
        }
    }

    // Expected results, computed once with the ordinary pools. Combinations a codec cannot do (for example a format
    // without alpha through an op) are left out.
    private static Dictionary<(string, string), byte[]> ComputeExpected(List<Input> inputs, List<Op> ops)
    {
        var expected = new Dictionary<(string, string), byte[]>();
        foreach (Input input in inputs)
        {
            foreach (Op op in ops)
            {
                byte[]? d = TryRun(op, input, CancellationToken.None, out _);
                if (d != null)
                {
                    expected[(input.Name, op.Name)] = d;
                }
            }
        }

        return expected;
    }

    [Fact]
    public void ThePoisoningPoolReportsWritesAfterReturnAndDoubleReturns()
    {
        var violations = new ConcurrentQueue<string>();
        var pool = new PoisoningPool<byte>(violations, captureStacks: true);
        byte[] a = pool.Rent(1000);
        pool.Return(a);
        a[5] = 1;
        pool.Sweep();
        Assert.Single(violations);
        Assert.Contains("write after return", violations.First());

        byte[] b = pool.Rent(1000);
        Assert.Same(a, b);
        pool.Return(b);
        pool.Return(b);
        Assert.Equal(2, violations.Count);
        Assert.Contains("double return", violations.Last());

        byte[] c = pool.Rent(1000);
        pool.Return(c);
        c[0] = 0;
        byte[] d = pool.Rent(1000);
        Assert.Same(c, d);
        Assert.Equal(3, violations.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProcessAndDrop(Op op, Input input)
    {
        // The page is garbage as soon as this returns; nothing disposes it.
        _ = TryRun(op, input, CancellationToken.None, out _);
    }

    [Fact]
    public void FinalizersOfTheLastPageNeverDisturbTheNextPagesDecode()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_POOLCHECK_ROUNDS"), out int r) ? Math.Max(r / 5, 20) : 20;
        List<Input> inputs = Inputs();
        List<Op> ops = Ops();
        Dictionary<(string, string), byte[]> expected = ComputeExpected(inputs, ops);
        Op[] firsts = [ops.First(o => o.Name == "whole-gray-bypass"), ops.First(o => o.Name == "stream-gray-bypass")];
        Input[] next = [.. inputs.Where(i => i.Name is "png-gray-pipelined" or "png-rgb-pipelined" or "jpeg" or "tiff" or "png-adam7" or "webp-lossy")];
        Op[] before = [.. ops];
        var problems = new ConcurrentQueue<string>();
        string[] violations;
        using (var check = new PoolCheck())
        using (var stop = new CancellationTokenSource())
        {
            // Collections and finalizer runs while the next page decodes, as a gen-0 collection after a large page does.
            var hammer = Task.Run(() =>
            {
                var rnd = new Random(9);
                while (!stop.IsCancellationRequested)
                {
                    GC.Collect(rnd.Next(3), GCCollectionMode.Forced, blocking: rnd.Next(4) != 0);
                    if (rnd.Next(2) == 0)
                    {
                        GC.WaitForPendingFinalizers();
                    }

                    Thread.Sleep(rnd.Next(3));
                }
            });
            var workers = Enumerable.Range(0, 3).Select(t => new Thread(() =>
            {
                var rnd = new Random(500 + t);
                for (int n = 0; n < rounds; n++)
                {
                    var (iname, oname) = expected.Keys.ElementAt(rnd.Next(expected.Count));
                    ProcessAndDrop(before.First(o => o.Name == oname), inputs.First(i => i.Name == iname));
                    Input page = next[rnd.Next(next.Length)];
                    Op first = firsts[rnd.Next(firsts.Length)];
                    byte[]? got = TryRun(first, page, CancellationToken.None, out Exception? error);
                    if (got == null)
                    {
                        problems.Enqueue($"{page.Name}: {error}");
                    }
                    else if (!got.AsSpan().SequenceEqual(expected[(page.Name, first.Name)]))
                    {
                        problems.Enqueue($"{page.Name}: first read after {iname}/{oname} differs from the reference");
                    }
                }
            })).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
            stop.Cancel();
            hammer.Wait();
            violations = check.Finish();
        }

        Assert.True(problems.IsEmpty, string.Join("\n---\n", problems.Take(5)));
        Assert.True(violations.Length == 0, string.Join("\n=====\n", violations));
    }

    [Fact]
    public void LibraryNeverWritesToAnArrayItHasReturned()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_POOLCHECK_ROUNDS"), out int r) ? r : 30;
        List<Input> inputs = Inputs();
        List<Op> ops = Ops();

        Dictionary<(string, string), byte[]> expected = ComputeExpected(inputs, ops);
        Assert.True(expected.Count >= inputs.Count * 6, $"only {expected.Count} combinations ran");
        var combos = expected.Keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToArray();

        // Failures injected into file reads of the calling flow (also seen by the threads it starts).
        HandleStream.TestReadAt = (SafeFileHandle handle, Span<byte> buffer, long offset) =>
        {
            int[]? box = s_readsUntilFault.Value;
            if (box != null && Interlocked.Decrement(ref box[0]) == 0)
            {
                throw new IOException("injected read failure");
            }

            return System.IO.RandomAccess.Read(handle, buffer, offset);
        };

        var problems = new ConcurrentQueue<string>();
        int succeeded = 0;
        int canceled = 0;
        int faulted = 0;
        string[] violations;
        long rents;
        using (var check = new PoolCheck())
        {
            var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
            {
                var rnd = new Random(100 + t);
                for (int n = 0; n < rounds; n++)
                {
                    var (iname, oname) = combos[rnd.Next(combos.Length)];
                    Input input = inputs.First(x => x.Name == iname);
                    Op op = ops.First(x => x.Name == oname);
                    using var cts = new CancellationTokenSource();
                    int mode = rnd.Next(4);
                    if (mode == 1)
                    {
                        cts.CancelAfter(TimeSpan.FromTicks(rnd.Next(0, 300_000)));
                    }
                    else if (mode == 2)
                    {
                        s_readsUntilFault.Value = [1 + rnd.Next(6)];
                    }

                    byte[]? got = TryRun(op, input, cts.Token, out Exception? error);
                    s_readsUntilFault.Value = null;
                    if (got != null)
                    {
                        Interlocked.Increment(ref succeeded);
                        if (!got.AsSpan().SequenceEqual(expected[(iname, oname)]))
                        {
                            problems.Enqueue($"{iname}/{oname}: result differs from the reference (mode {mode})");
                        }
                    }
                    else if (error is OperationCanceledException && mode == 1)
                    {
                        Interlocked.Increment(ref canceled);
                    }
                    else if (error is IOException && mode == 2)
                    {
                        Interlocked.Increment(ref faulted);
                    }
                    else
                    {
                        problems.Enqueue($"{iname}/{oname}: mode {mode}: {error}");
                    }
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            violations = check.Finish();
            rents = check.Rents;
        }

        TestContext.Current.SendDiagnosticMessage($"pool check: {combos.Length} combinations, {succeeded} identical results, {canceled} cancelled, {faulted} read failures, {rents} rents");
        Assert.True(succeeded > rounds && canceled > 0 && faulted > 0, $"pool check: {combos.Length} combinations, {succeeded} identical results, {canceled} cancelled, {faulted} read failures");
        Assert.True(rents > 100, "the poisoning pools were not used");
        Assert.True(problems.IsEmpty, string.Join("\n---\n", problems.Take(5)));
        Assert.True(violations.Length == 0, string.Join("\n=====\n", violations));
    }
}
