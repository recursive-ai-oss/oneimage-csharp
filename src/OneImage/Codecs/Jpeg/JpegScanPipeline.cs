using System.Buffers;
using System.Runtime.ExceptionServices;

namespace OneImage.Codecs.Jpeg;

/// <summary>
/// Decodes the scans of a progressive Huffman JPEG side by side. A scan only depends on the earlier scans that wrote the same
/// coefficients of the same component (its first scan and the refinements before it), and only block by block, so a refinement
/// scan can follow the scan it refines a few block rows behind while other scans, of other components or other frequency bands,
/// run next to them. Each scan gets its own reader over a private copy of its entropy-coded bytes. The coefficients written, and the
/// way a damaged file fails, are those of decoding the scans one after the other:
/// <list type="bullet">
/// <item>Scans are only taken on when the file follows the progression rules (each coefficient is first coded once, then refined one
/// bit position at a time, in order). At the first scan that does not, the scans before it are finished and the rest is left to the
/// sequential decoder.</item>
/// <item>Errors are ranked by scan: the failure of the earliest failing scan is the one thrown, the way the sequential decoder
/// stops at the first. Scans after a failed scan are abandoned.</item>
/// <item>Header errors found between scans are held back until the scans before them have been decoded.</item>
/// </list>
/// The pipeline is not used when the coefficient set is large against the compressed size, because the limit on lazily
/// allocated coefficient rows depends on how much of the file has been read when a row is first needed.
/// </summary>
internal sealed class JpegScanPipeline : IDisposable
{
    /// <summary>Test hook: 0 turns the pipeline off, a degree of 2 or more forces it (at any size), -1 chooses by size and the per-call degree.</summary>
    internal static int Override = -1;

    /// <summary>Counts scans decoded by a pipeline, so tests can prove it ran.</summary>
    internal static int PipelinedScans;

    // Below this many pixels, threads cost more than overlapping the scans saves.
    private const int MinPixels = 1 << 20;

    // Compressed bytes copied for scans that have not finished decoding; one scan is always allowed.
    private const long MaxQueuedBytes = 64L << 20;

    private sealed class Item
    {
        public int Index;
        public required JpegScanDecoder.ScanPlan Plan;
        public byte[] Data = [];
        public int Length;
        public bool Interleaved;
        public bool Claimed;

        // Set once no thread reads Data any more.
        public bool Finished;

        // Rows (MCU rows if interleaved, else block rows) decoded so far; int.MaxValue once the scan is finished.
        public int Rows;
        public (Item Pred, JpegComponent Comp)[] Deps = [];
    }

    private readonly JpegReader _reader;
    private readonly JpegHeaders _headers;
    private readonly JpegFrame _f;
    private readonly JpegTables _tables;
    private readonly JpegCoefficientStore _store;
    private readonly bool[] _dcOnly;
    private readonly ImageLimits _limits;
    private readonly CancellationToken _ct;
    private readonly int _degree;

    private readonly object _gate = new();
    private readonly List<Item> _items = [];
    private readonly List<(int Index, Exception Error)> _errors = [];
    private int _claimed;
    private bool _listDone;
    private int _failedMin = int.MaxValue;
    private long _queuedBytes;
    private Thread[] _threads = [];

    // Per component and coefficient: the point transform (Al) of the last scan that coded it, or -1.
    private readonly int[][] _chain;

    /// <summary>Decides whether the scans of this frame should go through a pipeline.</summary>
    public static bool Wanted(JpegFrame f, JpegReader reader, ImageLimits limits, out int degree)
    {
        degree = 0;
        int forced = Override;
        if (forced == 0 || !f.Progressive || f.Arithmetic)
        {
            return false;
        }

        degree = forced > 0 ? forced : Math.Min(ImageEngine.ResolveDegree(0), Environment.ProcessorCount);
        if (degree < 2 || (forced < 0 && (long)f.Width * f.Height < MinPixels))
        {
            return false;
        }

        // The sequential decoder refuses coefficient rows that the bytes read so far cannot explain. Here rows are first touched
        // while other threads read, so that check must never be the thing that fires: take the pipeline only when it cannot.
        double ratio = limits.MaxExpansionRatio;
        if (!double.IsPositiveInfinity(ratio))
        {
            double total = 0;
            foreach (JpegComponent c in f.Components)
            {
                total += (double)c.BlockRows * c.BlocksPerLine * 64 * sizeof(short);
            }

            if (total > ratio * (65536.0 + reader.BytesRead))
            {
                return false;
            }
        }

        return true;
    }

    public JpegScanPipeline(JpegReader reader, JpegHeaders headers, JpegFrame f, JpegTables tables, JpegCoefficientStore store, bool[] dcOnly, ImageLimits limits, int degree, CancellationToken ct)
    {
        _reader = reader;
        _headers = headers;
        _f = f;
        _tables = tables;
        _store = store;
        _dcOnly = dcOnly;
        _limits = limits;
        _degree = degree;
        _ct = ct;
        _chain = new int[f.Components.Length][];
        for (int i = 0; i < _chain.Length; i++)
        {
            _chain[i] = new int[64];
            Array.Fill(_chain[i], -1);
        }
    }

    /// <summary>
    /// Decodes <paramref name="first"/> and the scans after it for as long as they follow the progression rules.
    /// </summary>
    /// <param name="first">The first scan, its header read; the reader sits at its entropy-coded data.</param>
    /// <param name="rest">When this returns <see langword="true"/>, the scan the sequential decoder should continue with (header read, reader at its data).</param>
    /// <returns><see langword="true"/> if <paramref name="rest"/> is set; <see langword="false"/> when the last scan has been decoded.</returns>
    /// <param name="postRow">
    /// Called with each MCU row, in order, as soon as every scan has finished it, while later rows are still being decoded; returning
    /// false means nothing more is wanted. Only used when the whole scan list turned out to follow the progression rules and no scan fails;
    /// rows delivered before a failure stay delivered. Exceptions it throws surface after any scan failure.
    /// </param>
    /// <param name="posted">The number of leading MCU rows given to <paramref name="postRow"/>.</param>
    /// <param name="stopped">Whether <paramref name="postRow"/> returned false.</param>
    public bool Run(JpegScan first, Func<int, bool>? postRow, out int posted, out bool stopped, out JpegScan rest)
    {
        rest = null!;
        posted = 0;
        stopped = false;
        Exception? postError = null;
        JpegScan? fallback = null;
        Exception? held = null;
        var decoder = new JpegScanDecoder(_reader, _f, _tables, _store) { DcOnlyComponents = _dcOnly.Contains(true) ? _dcOnly : null };
        JpegScan scan = first;
        try
        {
            while (true)
            {
                if (!Accept(scan))
                {
                    fallback = scan;
                    break;
                }

                try
                {
                    JpegDecoderCore.LatchQuantTables(scan, _tables);
                    if (scan.Ss > 0 && scan.Components.Length == 1 && _dcOnly[scan.Components[0].Index])
                    {
                        // Only the DC image of this component is wanted: its AC scans are stepped over, as in the sequential decoder.
                        _reader.SkipEntropyData();
                    }
                    else
                    {
                        Publish(decoder, scan);
                    }
                }
                catch (Exception ex)
                {
                    held = ex;
                    break;
                }

                bool more;
                try
                {
                    more = JpegDecoderCore.NextScan(_reader, _headers, _f, _tables, _limits, out scan);
                }
                catch (Exception ex)
                {
                    held = ex;
                    break;
                }

                if (!more)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _listDone = true;
                Monitor.PulseAll(_gate);
            }

            try
            {
                if (postRow is not null && fallback is null && held is null)
                {
                    posted = PostFinishedRows(postRow, out stopped, out postError);
                }
            }
            finally
            {
                foreach (Thread t in _threads)
                {
                    t.Join();
                }

                lock (_gate)
                {
                    ReleaseFinished();
                }
            }
        }

        // The earliest failing scan wins; a failure while reading the headers after the last started scan comes after all of them.
        Exception? error = null;
        int best = int.MaxValue;
        foreach ((int index, Exception e) in _errors)
        {
            if (index < best)
            {
                best = index;
                error = e;
            }
        }

        error ??= held ?? postError;
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        if (fallback is not null)
        {
            rest = fallback;
            return true;
        }

        return false;
    }

    // Hands MCU rows to postRow in order once every scan has finished them. Returns the number handed over.
    private int PostFinishedRows(Func<int, bool> postRow, out bool stopped, out Exception? error)
    {
        stopped = false;
        error = null;
        int r = 0;
        for (; r < _f.McusY; r++)
        {
            lock (_gate)
            {
                while (!RowFinished(r))
                {
                    if (_failedMin != int.MaxValue)
                    {
                        return r;
                    }

                    Monitor.Wait(_gate);
                }
            }

            try
            {
                _ct.ThrowIfCancellationRequested();
                if (!postRow(r))
                {
                    stopped = true;
                    return r + 1;
                }
            }
            catch (Exception ex)
            {
                // Ranked after the scans' own failures, as the transform comes after the scans in the sequential decoder.
                error = ex;
                return r;
            }
        }

        return r;
    }

    // Whether every scan has finished the block rows of MCU row r, for every component it codes.
    private bool RowFinished(int r)
    {
        foreach (Item item in _items)
        {
            foreach (JpegComponent c in item.Plan.Comps)
            {
                long need = (r + 1L) * c.V;
                if (!item.Interleaved)
                {
                    need = Math.Min(need, c.HeightInBlocks);
                }

                if (DoneBlockRows(item, c) < need)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Whether the scan follows the progression rules given the scans accepted so far (T.81 G.1.1.1.1 and G.1.2); records it if so.
    private bool Accept(JpegScan s)
    {
        JpegComponent[] comps = s.Components;
        if (comps.Length == 0 || s.Al is < 0 or > 13 || s.Ah is < 0 or > 13)
        {
            return false;
        }

        for (int i = 0; i < comps.Length; i++)
        {
            for (int j = i + 1; j < comps.Length; j++)
            {
                if (comps[i] == comps[j])
                {
                    return false;
                }
            }
        }

        if (s.Ss == 0)
        {
            if (s.Se != 0)
            {
                return false;
            }
        }
        else if (comps.Length != 1 || s.Ss > s.Se || s.Se > 63)
        {
            return false;
        }

        foreach (JpegComponent c in comps)
        {
            int[] chain = _chain[c.Index];
            for (int k = s.Ss; k <= s.Se; k++)
            {
                if (s.Ah == 0 ? chain[k] != -1 : chain[k] != s.Ah || s.Al != s.Ah - 1)
                {
                    return false;
                }
            }
        }

        foreach (JpegComponent c in comps)
        {
            Array.Fill(_chain[c.Index], s.Al, s.Ss, s.Se - s.Ss + 1);
        }

        return true;
    }

    private void Publish(JpegScanDecoder decoder, JpegScan scan)
    {
        JpegScanDecoder.ScanPlan plan = decoder.Prepare(scan);

        lock (_gate)
        {
            ReleaseFinished();
            while (_queuedBytes > MaxQueuedBytes)
            {
                Monitor.Wait(_gate);
                ReleaseFinished();
            }
        }

        byte[] data = [];
        int len = 0;
        _reader.CopyScanData(ref data, ref len);

        var item = new Item { Plan = plan, Data = data, Length = len, Interleaved = scan.Components.Length > 1 };
        var deps = new List<(Item, JpegComponent)>();
        foreach (JpegComponent c in scan.Components)
        {
            foreach (Item p in _items)
            {
                if (p.Plan.Scan.Components.Contains(c) && p.Plan.Scan.Ss <= LastWritten(scan) && scan.Ss <= LastWritten(p.Plan.Scan))
                {
                    deps.Add((p, c));
                }
            }
        }

        item.Deps = [.. deps];
        lock (_gate)
        {
            item.Index = _items.Count;
            _items.Add(item);
            _queuedBytes += len;
            if (_threads.Length == 0)
            {
                _threads = new Thread[_degree];
                for (int i = 0; i < _threads.Length; i++)
                {
                    _threads[i] = new Thread(Work) { IsBackground = true, Name = "oneimage-jpeg-scan" };
                    _threads[i].Start();
                }
            }

            Monitor.PulseAll(_gate);
        }
    }

    // The last zigzag index a scan can change. A first AC scan only stops at its end-of-band index between symbols: a damaged run length can
    // carry it past that index (up to 63), so it can touch coefficients that a later scan's band names. Refinement scans stay within their band.
    private static int LastWritten(JpegScan s) => s.Ss > 0 && s.Ah == 0 ? 63 : s.Se;

    private void Work()
    {
        while (true)
        {
            Item? item;
            lock (_gate)
            {
                while ((item = Claim()) is null && !(_listDone && _claimed == _items.Count))
                {
                    Monitor.Wait(_gate);
                }

                if (item is null)
                {
                    return;
                }
            }

            DecodeItem(item);
        }
    }

    // The biggest scan that no thread has taken and whose predecessors have all been taken. Big scans are the long ones, so starting
    // them first shortens the whole run; waiting only for predecessors that are already running cannot deadlock, because the running
    // scan with the lowest index never waits for anything that is not finished and running.
    private Item? Claim()
    {
        Item? best = null;
        foreach (Item it in _items)
        {
            if (it.Claimed || (best is not null && it.Length <= best.Length))
            {
                continue;
            }

            bool ready = true;
            foreach ((Item pred, _) in it.Deps)
            {
                if (!pred.Claimed)
                {
                    ready = false;
                    break;
                }
            }

            if (ready)
            {
                best = it;
            }
        }

        if (best is not null)
        {
            best.Claimed = true;
            _claimed++;
        }

        return best;
    }

    private void DecodeItem(Item item)
    {
        using var reader = JpegReader.CreateDetached();
        try
        {
            if (item.Index > Volatile.Read(ref _failedMin))
            {
                return;
            }

            reader.Attach(item.Data, 0, item.Length);
            var decoder = new JpegScanDecoder(reader, _f, _tables, _store);
            Interlocked.Increment(ref PipelinedScans);
            bool done = decoder.Run(item.Plan, row => Progress(item, row + 1), my => Ready(item, my), _ct);
            if (done)
            {
                Progress(item, int.MaxValue);
            }
        }
        catch (Exception ex)
        {
            // Handed to the calling thread, which rethrows the earliest one unchanged.
            lock (_gate)
            {
                _errors.Add((item.Index, ex));
                if (item.Index < _failedMin)
                {
                    _failedMin = item.Index;
                }

                Monitor.PulseAll(_gate);
            }
        }
        finally
        {
            // The array goes back to the pool from the calling thread (see ReleaseFinished): a pool keeps the arrays handed back by a
            // thread in that thread's own cache, which is lost when this short-lived thread ends.
            lock (_gate)
            {
                item.Finished = true;
                _queuedBytes -= item.Length;
                Monitor.PulseAll(_gate);
            }
        }
    }

    // Gives the copies of finished scans back to the pool. Called with the lock held, on the calling thread.
    private void ReleaseFinished()
    {
        foreach (Item it in _items)
        {
            if (it.Finished && it.Data.Length > 0)
            {
                Pools<byte>.Pool.Return(it.Data);
                it.Data = [];
            }
        }
    }

    private bool Progress(Item item, int rows)
    {
        lock (_gate)
        {
            item.Rows = rows;
            Monitor.PulseAll(_gate);
        }

        return true;
    }

    // Block rows of component c that scan p has finished.
    private static long DoneBlockRows(Item p, JpegComponent c)
    {
        int rows = Volatile.Read(ref p.Rows);
        return rows == int.MaxValue ? long.MaxValue : p.Interleaved ? (long)rows * c.V : rows;
    }

    // Whether the scans that `item` refines have finished the block rows that its row `my` reads.
    private static bool Satisfied(Item item, int my)
    {
        foreach ((Item pred, JpegComponent c) in item.Deps)
        {
            long need = item.Interleaved ? ((long)(my + 1)) * c.V : my + 1;
            if (DoneBlockRows(pred, c) < need)
            {
                return false;
            }
        }

        return true;
    }

    private bool Ready(Item item, int my)
    {
        if (item.Deps.Length == 0)
        {
            return item.Index <= Volatile.Read(ref _failedMin);
        }

        if (Satisfied(item, my))
        {
            return true;
        }

        lock (_gate)
        {
            while (!Satisfied(item, my))
            {
                if (item.Index > _failedMin)
                {
                    return false;
                }

                Monitor.Wait(_gate);
            }

            return item.Index <= _failedMin;
        }
    }

    public void Dispose()
    {
        // Run joins its threads before returning or throwing; this only guards a path that never reached it.
        lock (_gate)
        {
            _listDone = true;
            _failedMin = -1;
            Monitor.PulseAll(_gate);
        }

        foreach (Thread t in _threads)
        {
            t.Join();
        }
    }
}
