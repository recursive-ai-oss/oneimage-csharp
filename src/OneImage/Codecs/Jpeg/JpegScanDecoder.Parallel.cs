using System.Buffers;
using System.Runtime.ExceptionServices;

namespace OneImage.Codecs.Jpeg;

/// <summary>How a baseline scan with restart intervals is split over worker threads.</summary>
/// <param name="Degree">Worker threads.</param>
/// <param name="ChunkIntervals">Restart intervals per work unit.</param>
/// <param name="ChunkMcus">MCUs per work unit (a multiple of the restart interval, so a unit starts on an interval boundary).</param>
/// <param name="Chunks">Number of work units.</param>
/// <param name="Sets">MCU rows the coefficient ring must hold.</param>
/// <param name="MaxInFlight">Work units loaded at once.</param>
internal readonly record struct JpegParallelPlan(int Degree, int ChunkIntervals, long ChunkMcus, int Chunks, int Sets, int MaxInFlight);

internal sealed partial class JpegScanDecoder
{
    // Below this many pixels, starting worker threads costs more than splitting the entropy decoding saves.
    internal const int ParallelMinPixels = 1 << 20;

    /// <summary>Test hook: 0 turns parallel restart-interval decoding off, a degree of 2 or more forces it (at any size), -1 chooses by size and the per-call degree.</summary>
    internal static int ParallelOverride = -1;

    /// <summary>Test hook: when positive, the MCUs a work unit aims for (rounded up to whole restart intervals).</summary>
    internal static int ParallelChunkOverride;

    /// <summary>Counts scans decoded by <see cref="DecodeParallel"/>, so tests can prove the parallel path ran.</summary>
    internal static int ParallelScans;

    // The coefficient ring may not grow past this many bytes.
    private const long MaxRingBytes = 16L << 20;

    // Compressed bytes held for work units that have not been retired yet; one unit is always allowed.
    private const long MaxLoadedBytes = 16L << 20;

    /// <summary>
    /// Decides whether the sequential single-scan Huffman <paramref name="scan"/> of <paramref name="f"/> is worth decoding in
    /// parallel. Restart intervals are independent (DC predictors restart, the bit buffer is flushed), so units of whole
    /// intervals can be decoded at the same time.
    /// </summary>
    public static bool TryPlan(JpegFrame f, JpegScan scan, int restartInterval, out JpegParallelPlan plan)
    {
        plan = default;
        int forced = ParallelOverride;
        if (forced == 0 || restartInterval <= 0 || f.Progressive || f.Arithmetic)
        {
            return false;
        }

        JpegComponent[] comps = scan.Components;
        bool interleaved = comps.Length > 1;
        if (!interleaved && (comps[0].H != 1 || comps[0].V != 1))
        {
            return false;
        }

        int degree = forced > 0 ? forced : Math.Min(ImageEngine.ResolveDegree(0), Environment.ProcessorCount);
        if (degree < 2 || (forced < 0 && (long)f.Width * f.Height < ParallelMinPixels))
        {
            return false;
        }

        long perRow = interleaved ? f.McusX : comps[0].WidthInBlocks;
        long rows = interleaved ? f.McusY : comps[0].HeightInBlocks;
        long total = perRow * rows;
        if (total <= restartInterval || perRow <= 0)
        {
            return false;
        }

        long target = ParallelChunkOverride > 0 ? ParallelChunkOverride : Math.Max(perRow, 128);
        long chunkIntervals = Math.Max(1, (target + restartInterval - 1) / restartInterval);
        long chunkMcus = chunkIntervals * restartInterval;
        long chunks = (total + chunkMcus - 1) / chunkMcus;
        if (chunks < 2 || chunkIntervals > int.MaxValue || chunks > int.MaxValue)
        {
            return false;
        }

        long rowBytes = 0;
        foreach (JpegComponent c in comps)
        {
            rowBytes += (long)c.BlocksPerLine * c.V * 64 * sizeof(short);
        }

        // A unit touches at most rowsPerChunk + 1 MCU rows; the ring holds the units in flight plus slack for the transform stage.
        long rowsPerChunk = (chunkMcus + perRow - 1) / perRow;
        int maxInFlight = (2 * degree) + 1;
        long sets = (((long)maxInFlight * chunkMcus + perRow - 1) / perRow) + rowsPerChunk + 3;
        if (sets * rowBytes > MaxRingBytes)
        {
            return false;
        }

        plan = new JpegParallelPlan(degree, (int)chunkIntervals, chunkMcus, (int)chunks, (int)sets, maxInFlight);
        return true;
    }

    private sealed class ChunkSlot
    {
        public byte[] Data = [];
        public int Len;
        public int Status; // 0 loaded or running, 2 decoded, 3 failed
        public Exception? Error;
        public long FailMcu;
    }

    private sealed class ParallelState
    {
        public readonly object Gate = new();
        public readonly ChunkSlot[] Slots;
        public int Loaded;
        public int NextClaim;
        public bool Stop;
        public bool NoMore;

        public ParallelState(int slots)
        {
            Slots = new ChunkSlot[slots];
            for (int i = 0; i < slots; i++)
            {
                Slots[i] = new ChunkSlot();
            }
        }
    }

    /// <summary>
    /// Decodes a whole baseline scan with several threads. The calling thread cuts the compressed data into work units of
    /// whole restart intervals, workers decode units into the coefficient ring, and finished units are handed to
    /// <paramref name="post"/> as complete MCU rows, in order. The result and the way it fails match
    /// <see cref="Decode"/> exactly: a worker runs the same per-MCU loop over the same bytes, and when a unit fails, the rows
    /// before the failing MCU are delivered first, then the failure of the earliest failing unit is thrown.
    /// </summary>
    /// <param name="scan">The scan.</param>
    /// <param name="plan">From <see cref="TryPlan"/>; the store must have been created with <c>plan.Sets</c> MCU rows.</param>
    /// <param name="post">Called with each completed MCU row in order; false stops decoding.</param>
    /// <param name="transformed">The number of leading MCU rows whose coefficients may be overwritten.</param>
    /// <param name="waitTransformed">Blocks until that many rows are transformed; false when decoding should stop.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="false"/> if stopped early by the callback.</returns>
    public bool DecodeParallel(JpegScan scan, in JpegParallelPlan plan, Func<int, bool> post, Func<int> transformed, Func<int, bool> waitTransformed, CancellationToken ct)
    {
        Mode mode = PrepareTables(scan, out JpegComponent[] comps, out JpegHuffmanTable?[] dcT, out JpegHuffmanTable?[] acT);
        if (mode != Mode.Baseline)
        {
            throw new InvalidOperationException("Parallel decoding handles sequential Huffman scans only.");
        }

        for (int i = 0; i < comps.Length; i++)
        {
            // Built on first use otherwise, which workers would race to do.
            if (acT[i] is { } t && DcOnlyComponents is { } d && d[comps[i].Index])
            {
                _ = t.Skip;
            }
        }

        bool interleaved = comps.Length > 1;
        long perRow = interleaved ? _f.McusX : comps[0].WidthInBlocks;
        long rows = interleaved ? _f.McusY : comps[0].HeightInBlocks;
        long total = perRow * rows;
        int ri = _t.RestartInterval;
        var st = new ParallelState(plan.MaxInFlight);
        JpegParallelPlan p = plan;
        Interlocked.Increment(ref ParallelScans);
        _r.ResetBits();
        _r.ClearPendingMarker();
        var threads = new Thread[p.Degree];
        try
        {
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() => Work(st, p, comps, dcT, acT, total, perRow, ri)) { IsBackground = true };
                threads[i].Start();
            }

            return Coordinate(st, p, scan, total, perRow, ri, post, transformed, waitTransformed, ct);
        }
        finally
        {
            lock (st.Gate)
            {
                st.Stop = true;
                Monitor.PulseAll(st.Gate);
            }

            foreach (Thread? th in threads)
            {
                th?.Join();
            }

            foreach (ChunkSlot s in st.Slots)
            {
                if (s.Data.Length > 0)
                {
                    Pools<byte>.Pool.Return(s.Data);
                    s.Data = [];
                }
            }
        }
    }

    private bool Coordinate(ParallelState st, in JpegParallelPlan p, JpegScan scan, long total, long perRow, int ri, Func<int, bool> post, Func<int> transformed, Func<int, bool> waitTransformed, CancellationToken ct)
    {
        ChunkSlot[] slots = st.Slots;
        int nChunks = p.Chunks;
        int nextLoad = 0;
        int frontier = 0;
        int postedRows = 0;
        bool ended = false;
        long loadedBytes = 0;
        long need = 0;
        while (frontier < nChunks)
        {
            ct.ThrowIfCancellationRequested();

            // Cut more units while the ring and the byte budget allow.
            bool loaded = false;
            while (!ended && nextLoad < nChunks && nextLoad - frontier < slots.Length && (nextLoad == frontier || loadedBytes < MaxLoadedBytes))
            {
                long a = (long)nextLoad * p.ChunkMcus;
                long b = Math.Min(a + p.ChunkMcus, total);

                // Writing the unit's last MCU row reuses the ring slot of the row Sets earlier; that row must be transformed by then.
                need = ((b - 1) / perRow) + 1 - p.Sets;
                if (need > transformed())
                {
                    break;
                }

                ChunkSlot slot = slots[nextLoad % slots.Length];
                slot.Len = 0;
                slot.Status = 0;
                slot.Error = null;
                int wanted = (int)((b - a + ri - 1) / ri);
                _r.CopyEntropyIntervals(wanted, ref slot.Data, ref slot.Len, out bool stop);
                loadedBytes += slot.Len;
                if (stop)
                {
                    // No more intervals follow. The unit that ran out of data fails at the missing restart marker, or is the last one.
                    ended = true;
                    nChunks = nextLoad + 1;
                }

                lock (st.Gate)
                {
                    st.Loaded = ++nextLoad;
                    st.NoMore = ended || nextLoad == nChunks;
                    Monitor.PulseAll(st.Gate);
                }

                loaded = true;
            }

            // Hand over the oldest unit if it is finished.
            ChunkSlot? done = null;
            lock (st.Gate)
            {
                if (frontier < nextLoad && slots[frontier % slots.Length].Status != 0)
                {
                    done = slots[frontier % slots.Length];
                }
            }

            if (done is not null)
            {
                long end = Math.Min(((long)frontier + 1) * p.ChunkMcus, total);
                loadedBytes -= done.Len;
                if (done.Status == 2)
                {
                    if (!PostRows(ref postedRows, (int)(end / perRow), post, ct))
                    {
                        return false;
                    }

                    frontier++;
                    continue;
                }

                // Rows that end before the failing MCU were complete in the sequential decoder too.
                if (!PostRows(ref postedRows, (int)(done.FailMcu / perRow), post, ct))
                {
                    return false;
                }

                ExceptionDispatchInfo.Capture(done.Error!).Throw();
            }

            if (loaded)
            {
                continue;
            }

            if (frontier == nextLoad)
            {
                if (!ended && nextLoad < nChunks)
                {
                    // Nothing in flight: only the transform stage is holding up the next unit.
                    if (!waitTransformed((int)Math.Max(need, 0)))
                    {
                        return false;
                    }

                    continue;
                }

                break;
            }

            lock (st.Gate)
            {
                while (slots[frontier % slots.Length].Status == 0)
                {
                    Monitor.Wait(st.Gate);
                }
            }
        }

        if (postedRows < (int)(total / perRow))
        {
            throw new ImageFormatException("JPEG scan data ended early.");
        }

        return true;
    }

    private static bool PostRows(ref int posted, int upTo, Func<int, bool> post, CancellationToken ct)
    {
        while (posted < upTo)
        {
            ct.ThrowIfCancellationRequested();
            if (!post(posted++))
            {
                return false;
            }
        }

        return true;
    }

    private void Work(ParallelState st, JpegParallelPlan p, JpegComponent[] comps, JpegHuffmanTable?[] dcT, JpegHuffmanTable?[] acT, long total, long perRow, int ri)
    {
        JpegReader r = JpegReader.CreateDetached();
        int[] preds = new int[comps.Length];
        while (true)
        {
            int k;
            ChunkSlot slot;
            lock (st.Gate)
            {
                while (st.NextClaim >= st.Loaded && !st.Stop && !st.NoMore)
                {
                    Monitor.Wait(st.Gate);
                }

                if (st.Stop || st.NextClaim >= st.Loaded)
                {
                    return;
                }

                k = st.NextClaim++;
                slot = st.Slots[k % st.Slots.Length];
            }

            long a = (long)k * p.ChunkMcus;
            long b = Math.Min(a + p.ChunkMcus, total);
            Exception? error = null;
            try
            {
                DecodeChunk(r, preds, comps, dcT, acT, slot, a, b, total, perRow, ri);
            }
            catch (Exception ex)
            {
                // Handed to the coordinating thread, which rethrows it unchanged once the rows before it are delivered.
                error = ex;
            }

            lock (st.Gate)
            {
                slot.Error = error;
                slot.Status = error is null ? 2 : 3;
                Monitor.PulseAll(st.Gate);
            }
        }
    }

    private static bool Mark(ChunkSlot slot, long mcu)
    {
        slot.FailMcu = mcu;
        return false;
    }

    /// <summary>The per-MCU loop of <see cref="Decode"/> over MCUs [a, b) of one cut of the data, with this worker's own bit reader and DC predictors.</summary>
    private void DecodeChunk(JpegReader r, int[] preds, JpegComponent[] comps, JpegHuffmanTable?[] dcT, JpegHuffmanTable?[] acT, ChunkSlot slot, long a, long b, long total, long perRow, int ri)
    {
        r.Attach(slot.Data, 0, slot.Len);
        Array.Clear(preds);
        bool interleaved = comps.Length > 1;
        bool[]? dcOnly = DcOnlyComponents;
        long m = a;
        try
        {
            for (; m < b; m++)
            {
                if (m != a && m % ri == 0)
                {
                    r.ProcessRestart();
                    Array.Clear(preds);
                }

                long my = m / perRow;
                int mx = (int)(m - (my * perRow));
                if (interleaved)
                {
                    for (int ci = 0; ci < comps.Length; ci++)
                    {
                        JpegComponent c = comps[ci];
                        bool dcc = dcOnly is not null && dcOnly[c.Index];
                        for (int by = 0; by < c.V; by++)
                        {
                            Span<short> row = _store.Row(c, (int)(my * c.V) + by);
                            for (int bx = 0; bx < c.H; bx++)
                            {
                                r.DecodeBlockBaseline(row.Slice(((mx * c.H) + bx) * 64, 64), ref preds[ci], dcT[ci]!, acT[ci]!, dcc);
                            }
                        }
                    }
                }
                else
                {
                    JpegComponent c = comps[0];
                    r.DecodeBlockBaseline(_store.Row(c, (int)my).Slice(mx * 64, 64), ref preds[0], dcT[0]!, acT[0]!, dcOnly is not null && dcOnly[c.Index]);
                }

                if (r.Overrun)
                {
                    throw new ImageFormatException("JPEG scan data ended early.");
                }
            }

            if (b < total)
            {
                // The marker that ends the unit's last interval; the sequential decoder checks it before the next MCU.
                r.ProcessRestart();
            }
        }
        catch (Exception) when (Mark(slot, m))
        {
            throw;
        }
    }
}
