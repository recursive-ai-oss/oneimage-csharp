namespace OneImage;

/// <summary>
/// Decides when the memory of an evicted spill tile may be reused. A reader gets a tile's bytes as a <see cref="Span{T}"/> and the
/// store cannot see when it is done with it, so a buffer taken off an evicted tile might still be read for a while. Readers that
/// can say how long they read (the layer renderer and the copy-out of a rendered tile) wrap that work in a <see cref="Scope"/>.
/// An evicted buffer waits in the epoch that was current at eviction; an epoch is closed once it has collected a little memory, and
/// its buffers are reused when it and every earlier epoch have no reader left. A reader that did not announce itself marks its tile
/// instead (<see cref="Tile"/> remembers that), and the buffer of such a tile is simply left to the garbage collector, which keeps it
/// alive for as long as any span points at it.
/// Reusable buffers sit in a small free list that rentals and page-ins draw from, so a steady working set recycles its own memory and
/// the collector has nothing to find.
/// </summary>
internal static class SpillEpochs
{
    /// <summary>An epoch is closed once this many bytes have been evicted into it.</summary>
    private const long Quantum = 1L << 20;

    /// <summary>Evicted bytes waiting for readers to leave; past this the oldest waiting buffers go to the garbage collector instead.</summary>
    private const long LimboCap = 32L << 20;

    /// <summary>Idle reusable bytes kept for rentals; past this returned buffers go to the shared pool.</summary>
    private const long FreeCap = 64L << 20;

    private static readonly object s_gate = new();
    private static readonly Queue<Epoch> s_closed = new();
    private static readonly Dictionary<int, Stack<byte[]>> s_free = [];
    private static Epoch s_current = new();
    private static long s_limboBytes;
    private static long s_freeBytes;

    [ThreadStatic]
    private static int t_depth;

    private sealed class Epoch
    {
        public int Readers;
        public int Closed;
        public List<byte[]>? Limbo;
        public long Bytes;
    }

    /// <summary>Gets a value indicating whether the calling thread is inside a <see cref="Scope"/>.</summary>
    public static bool InScope => t_depth > 0;

    /// <summary>Bytes of evicted buffers waiting for readers (diagnostics).</summary>
    public static long LimboBytes
    {
        get { lock (s_gate) { return s_limboBytes; } }
    }

    /// <summary>Bytes of idle reusable buffers (diagnostics).</summary>
    public static long FreeBytes
    {
        get { lock (s_gate) { return s_freeBytes; } }
    }

    /// <summary>
    /// Announces that the calling thread reads spill tiles until the scope is disposed. Spans taken from spill tiles inside the scope
    /// must not be used after it. Scopes nest and must be disposed on the thread that made them.
    /// </summary>
    public static Scope Enter()
    {
        Epoch e;
        while (true)
        {
            e = Volatile.Read(ref s_current);
            Interlocked.Increment(ref e.Readers);

            // Closed after our increment: the closer may already have decided the epoch is empty, so this one does not count.
            if (Volatile.Read(ref e.Closed) == 0)
            {
                break;
            }

            Leave(e);
        }

        t_depth++;
        return new Scope(e);
    }

    /// <summary>The marker of an active read scope.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly object? _epoch;

        internal Scope(object epoch) => _epoch = epoch;

        public void Dispose()
        {
            if (_epoch is Epoch e)
            {
                t_depth--;
                Leave(e);
            }
        }
    }

    /// <summary>Takes an evicted buffer: it is reused once every reader that might hold a span of it has left.</summary>
    public static void Defer(byte[] buffer)
    {
        lock (s_gate)
        {
            Epoch c = s_current;
            (c.Limbo ??= []).Add(buffer);
            c.Bytes += buffer.Length;
            s_limboBytes += buffer.Length;
            if (c.Bytes >= Quantum)
            {
                Volatile.Write(ref s_current, new Epoch());
                Interlocked.Exchange(ref c.Closed, 1);
                s_closed.Enqueue(c);
            }

            ReclaimLocked();
            if (s_limboBytes > LimboCap)
            {
                // A reader has been inside a scope for a long time. Leave the oldest waiting buffers to the collector.
                foreach (Epoch old in s_closed)
                {
                    if (old.Limbo != null)
                    {
                        s_limboBytes -= old.Bytes;
                        old.Bytes = 0;
                        old.Limbo = null;
                        if (s_limboBytes <= LimboCap / 2)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Offers a buffer that nobody can be reading (its tile is dead). Returns false when the free list is full.</summary>
    public static bool TryFree(byte[] buffer)
    {
        lock (s_gate)
        {
            return FreeLocked(buffer);
        }
    }

    /// <summary>Takes an idle buffer for a tile of <paramref name="length"/> bytes, or null.</summary>
    public static byte[]? Take(int length)
    {
        int size = RoundUp(length);
        lock (s_gate)
        {
            if (s_free.TryGetValue(size, out Stack<byte[]>? stack) && stack.TryPop(out byte[]? buf))
            {
                s_freeBytes -= buf.Length;
                return buf;
            }
        }

        return null;
    }

    private static int RoundUp(int length) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(4096, length));

    private static void Leave(Epoch e)
    {
        if (Interlocked.Decrement(ref e.Readers) == 0 && Volatile.Read(ref e.Closed) != 0)
        {
            lock (s_gate)
            {
                ReclaimLocked();
            }
        }
    }

    private static void ReclaimLocked()
    {
        while (s_closed.TryPeek(out Epoch? o) && Volatile.Read(ref o.Readers) == 0)
        {
            s_closed.Dequeue();
            if (o.Limbo != null)
            {
                foreach (byte[] b in o.Limbo)
                {
                    FreeLocked(b);
                }

                s_limboBytes -= o.Bytes;
                o.Limbo = null;
            }
        }
    }

    private static bool FreeLocked(byte[] buffer)
    {
        if (s_freeBytes + buffer.Length > FreeCap || buffer.Length != RoundUp(buffer.Length))
        {
            return false;
        }

        if (!s_free.TryGetValue(buffer.Length, out Stack<byte[]>? stack))
        {
            s_free[buffer.Length] = stack = new Stack<byte[]>();
        }

        stack.Push(buffer);
        s_freeBytes += buffer.Length;
        return true;
    }
}
