using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OneImage.Tests;

/// <summary>
/// A pool that finds misuse of rented arrays. Every returned array is filled with 0xFF and remembered with the stack of its
/// return. Renting it again, and a background sweep, check that the fill is intact: a write after the return is reported with
/// the stack of that return. Returning an array that is already free is reported as a double return. Arrays that are rented
/// for the first time are filled with a marker, so code that reads bytes it never wrote shows up as wrong output.
/// </summary>
internal sealed class PoisoningPool<T> : System.Buffers.ArrayPool<T>
    where T : unmanaged
{
    private const int MaxFreePerSize = 48;
    private readonly object _gate = new();
    private readonly Dictionary<int, Stack<T[]>> _free = [];
    private readonly Dictionary<T[], string> _returnedAt = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<string> _violations;
    private readonly bool _stacks;
    private long _rents;
    private long _returns;

    public PoisoningPool(ConcurrentQueue<string> violations, bool captureStacks)
    {
        _violations = violations;
        _stacks = captureStacks;
    }

    public long Rents => Interlocked.Read(ref _rents);

    public long Returns => Interlocked.Read(ref _returns);

    public override T[] Rent(int minimumLength)
    {
        if (minimumLength <= 0)
        {
            return [];
        }

        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, minimumLength));
        Interlocked.Increment(ref _rents);
        lock (_gate)
        {
            if (_free.TryGetValue(size, out Stack<T[]>? stack) && stack.Count > 0)
            {
                T[] a = stack.Pop();
                string where = _returnedAt[a];
                _returnedAt.Remove(a);
                if (!Intact(a))
                {
                    _violations.Enqueue($"write after return of {typeof(T).Name}[{a.Length}] (noticed on rent); returned at:\n{where}");
                }

                return a;
            }
        }

        T[] fresh = new T[size];
        MemoryMarshal.AsBytes(fresh.AsSpan()).Fill(0xA5);
        return fresh;
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        if (array.Length == 0)
        {
            return;
        }

        Interlocked.Increment(ref _returns);
        string here = _stacks ? new StackTrace(1, false).ToString() : "(stacks off)";
        lock (_gate)
        {
            if (_returnedAt.TryGetValue(array, out string? first))
            {
                _violations.Enqueue($"double return of {typeof(T).Name}[{array.Length}]; first returned at:\n{first}\nreturned again at:\n{here}");
                return;
            }

            if (!_free.TryGetValue(array.Length, out Stack<T[]>? stack))
            {
                _free[array.Length] = stack = new Stack<T[]>();
            }

            if (stack.Count >= MaxFreePerSize)
            {
                return;
            }

            MemoryMarshal.AsBytes(array.AsSpan()).Fill(0xFF);
            _returnedAt[array] = here;
            stack.Push(array);
        }
    }

    /// <summary>Checks every free array; a damaged one is reported once and filled again.</summary>
    public void Sweep()
    {
        lock (_gate)
        {
            foreach ((T[] a, string where) in _returnedAt)
            {
                if (!Intact(a))
                {
                    _violations.Enqueue($"write after return of {typeof(T).Name}[{a.Length}] (noticed by sweep); returned at:\n{where}");
                    MemoryMarshal.AsBytes(a.AsSpan()).Fill(0xFF);
                }
            }
        }
    }

    private static bool Intact(T[] a) => MemoryMarshal.AsBytes(a.AsSpan()).IndexOfAnyExcept((byte)0xFF) < 0;
}

/// <summary>Tile storage on top of a <see cref="PoisoningPool{T}"/>, so tile buffers get the same checks.</summary>
internal sealed class PoisoningTileStore(PoisoningPool<byte> pool) : ITileStore
{
    public byte[] Rent(int length, bool clear)
    {
        byte[] b = pool.Rent(length);
        if (clear)
        {
            b.AsSpan(0, length).Clear();
        }

        return b;
    }

    public void Return(byte[] buffer) => pool.Return(buffer);
}

/// <summary>
/// Replaces every array pool of the library (and the tile store and float scratch queues) with poisoning pools for the life of
/// the object, and sweeps them in the background. <see cref="Finish"/> returns every violation seen.
/// </summary>
internal sealed class PoolCheck : IDisposable
{
    private readonly ConcurrentQueue<string> _violations = new();
    private readonly List<Action> _sweeps = [];
    private readonly List<Action> _restore = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sweeper;
    private readonly PoisoningPool<byte> _bytes;
    private readonly PoisoningPool<float> _floats;

    public PoolCheck(bool captureStacks = true)
    {
        _bytes = Install<byte>(captureStacks);
        _floats = Install<float>(captureStacks);
        Install<int>(captureStacks);
        Install<short>(captureStacks);
        Install<ushort>(captureStacks);
        Install<uint>(captureStacks);
        Install<long>(captureStacks);
        Install<double>(captureStacks);
        Install<Rgba32>(captureStacks);

        ITileStore tiles = TileStores.Current;
        TileStores.Current = new PoisoningTileStore(_bytes);
        _restore.Add(() => TileStores.Current = tiles);
        FloatScratch.Override = _floats;
        _restore.Add(() => FloatScratch.Override = null);

        _sweeper = Task.Run(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (Action s in _sweeps)
                {
                    s();
                }

                Thread.Sleep(3);
            }
        });
    }

    public long Rents => _bytes.Rents + _floats.Rents;

    private PoisoningPool<T> Install<T>(bool stacks)
        where T : unmanaged
    {
        var pool = new PoisoningPool<T>(_violations, stacks);
        System.Buffers.ArrayPool<T> previous = Pools<T>.Pool;
        Pools<T>.Pool = pool;
        _restore.Add(() => Pools<T>.Pool = previous);
        _sweeps.Add(pool.Sweep);
        return pool;
    }

    /// <summary>Stops the sweeper, sweeps once more and returns the violations (first few, with stacks).</summary>
    public string[] Finish()
    {
        _stop.Cancel();
        _sweeper.Wait();
        foreach (Action s in _sweeps)
        {
            s();
        }

        return [.. _violations.Take(8)];
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _sweeper.Wait();
        }
        catch (AggregateException)
        {
        }

        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            _restore[i]();
        }
    }
}
