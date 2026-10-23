namespace OneImage.Codecs.Jpeg;

/// <summary>Drives a full decode: headers, scans, and the output pipeline.</summary>
internal static class JpegDecoderCore
{
    public static void Decode(Stream source, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken ct, Func<JpegFrame, JpegHeaders, JpegColorPlan>? planOverride = null)
    {
        int scale = request.ScaleDenominator;
        if (scale is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Scale denominator must be 1, 2, 4 or 8.");
        }

        using var reader = new JpegReader(source);
        var tables = new JpegTables();
        JpegHeaders h = JpegParser.ReadHeaders(reader, limits, tables, collectMetadata: false);
        JpegFrame f = h.Frame!;
        var plan = planOverride is null ? JpegColorPlan.Decide(f, h) : planOverride(f, h);
        if (f.Precision == 8)
        {
            Run<byte>(reader, h, f, plan, tables, request, sink, limits, ct);
        }
        else
        {
            Run<ushort>(reader, h, f, plan, tables, request, sink, limits, ct);
        }
    }

    // Below this many pixels the thread start costs more than the overlap saves.
    private const int OverlapMinPixels = 1 << 20;

    /// <summary>Test hook: 0 runs everything on the calling thread, 1 forces the two-stage split, 2 the three-stage split, -1 chooses by size and cores.</summary>
    internal static int StageOverride = -1;

    private static void Run<T>(JpegReader reader, JpegHeaders h, JpegFrame f, JpegColorPlan plan, JpegTables tables, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken ct)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        using var pipe = new JpegPipeline<T>(f, plan, request.ScaleDenominator, request.Region, sink);
        bool[] dcOnly = pipe.DcOnlyComponents();
        JpegScan scan = JpegParser.ParseSos(reader, f);

        // A sequential image with a single scan over all components streams MCU row by MCU row; everything else
        // (progressive, or components split over several scans) needs all coefficients before the first pixel row.
        bool streaming = !f.Progressive && scan.Components.Length == f.Components.Length;

        // Stages: entropy decoding, inverse transform, and upsampling/color/delivery. With three cores each gets a thread
        // (streaming); with two, the entropy thread also transforms. Progressive and multi-scan images transform after
        // the last scan, and overlap only the delivery with the transform.
        int stages = StageOverride >= 0 ? StageOverride : (long)f.Width * f.Height < OverlapMinPixels || Environment.ProcessorCount < 2 ? 0 : Environment.ProcessorCount >= 3 && streaming ? 2 : 1;
        // Baseline files with restart intervals decode their intervals on several threads into a deeper coefficient ring.
        JpegParallelPlan par = default;
        bool parallel = streaming && JpegScanDecoder.TryPlan(f, scan, tables.RestartInterval, out par);

        using var store = new JpegCoefficientStore(f, streaming, reader, limits, parallel ? par.Sets : streaming && stages > 0 ? 2 : 1);
        var decoder = new JpegScanDecoder(reader, f, tables, store) { DcOnlyComponents = dcOnly.Contains(true) ? dcOnly : null };
        using StagedTransform<T>? staged = stages > 0 ? new StagedTransform<T>(pipe, store, separateTransform: stages == 2 && streaming) : null;
        Func<int, bool>? onRow = staged is not null && streaming ? row => staged.Post(row) : streaming ? row => pipe.AddMcuRow(row, store) : null;
        int rowsPosted = 0;
        bool more = true;
        int firstUnposted = 0;
        bool stopPosting = false;
        if (f.Progressive && JpegScanPipeline.Wanted(f, reader, limits, out int pipelineDegree))
        {
            // Scans of one file that touch different coefficients, or follow each other row by row, decode side by side, and rows
            // go to the transform as the last scan finishes them. Whatever does not follow the progression rules is returned in
            // `scan` for the loop below.
            Func<int, bool> postRow = staged is not null ? row => staged.Post(row) : row => pipe.AddMcuRow(row, store);
            using var pipeline = new JpegScanPipeline(reader, h, f, tables, store, dcOnly, limits, pipelineDegree, ct);
            more = pipeline.Run(scan, postRow, out firstUnposted, out stopPosting, out scan);
        }

        while (more)
        {
            LatchQuantTables(scan, tables);
            if (f.Progressive && scan.Ss > 0 && scan.Components.Length == 1 && dcOnly[scan.Components[0].Index])
            {
                // This component only contributes its DC image at the requested size, so skip its AC scans without decoding them.
                reader.SkipEntropyData();
            }
            else
            {
                try
                {
                    if (parallel)
                    {
                        // The helper stages get the rows without making this thread wait for them: the decoder waits for transformed rows itself before reusing a ring slot.
                        Func<int, bool> post = staged is not null ? row => staged.Post(row, lag: int.MaxValue / 2) : row =>
                        {
                            rowsPosted = row + 1;
                            return pipe.AddMcuRow(row, store);
                        };
                        decoder.DecodeParallel(scan, par, post, staged is not null ? () => staged.Transformed : () => rowsPosted, staged is not null ? staged.WaitTransformed : _ => true, ct);
                    }
                    else
                    {
                        decoder.Decode(scan, onRow, ct);
                    }
                }
                catch (Exception ex) when (staged is not null && streaming && ex is not OperationCanceledException)
                {
                    // The entropy decoder runs up to two MCU rows ahead of the output. If it failed on data past the last row the
                    // request needs (a truncated file, a corrupt tail), the single-threaded path would have stopped before reaching
                    // it, so a request that the rows already handed over complete is a success.
                    if (!staged.TryComplete(request.Region.Height))
                    {
                        throw;
                    }
                }
            }

            if (streaming)
            {
                break;
            }

            more = NextScan(reader, h, f, tables, limits, out scan);
        }

        if (streaming)
        {
            staged?.Finish();
        }
        else if (staged is not null)
        {
            for (int r = firstUnposted; r < f.McusY && !stopPosting; r++)
            {
                ct.ThrowIfCancellationRequested();
                if (!staged.Post(r))
                {
                    break;
                }
            }

            staged.Finish();
        }
        else
        {
            for (int r = firstUnposted; r < f.McusY && !stopPosting; r++)
            {
                ct.ThrowIfCancellationRequested();
                if (!pipe.AddMcuRow(r, store))
                {
                    break;
                }
            }
        }

        if (pipe.RowsWritten != request.Region.Height)
        {
            throw new ImageFormatException("JPEG data ended before all rows were decoded.");
        }
    }

    internal static bool NextScan(JpegReader reader, JpegHeaders h, JpegFrame f, JpegTables tables, ImageLimits limits, out JpegScan scan)
    {
        while (true)
        {
            int m = reader.NextMarker();
            if (m is JpegConstants.Eoi or < 0)
            {
                scan = null!;
                return false;
            }

            if (m == JpegConstants.Sos)
            {
                scan = JpegParser.ParseSos(reader, f);
                return true;
            }

            if (m is JpegConstants.Soi or 0x01 || m is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            JpegParser.ParseSegment(reader, m, h, tables, limits, null);
        }
    }

    internal static void LatchQuantTables(JpegScan scan, JpegTables tables)
    {
        foreach (JpegComponent c in scan.Components)
        {
            c.Quant ??= tables.Quant[c.Tq] ?? throw new ImageFormatException("JPEG scan uses an undefined quantization table.");
        }
    }

    /// <summary>
    /// Runs the per-MCU-row work on helper threads while the calling thread entropy-decodes the next row. The inverse
    /// transform (stage 1) and upsampling/color/delivery (stage 2) are separate steps: either the calling thread does
    /// the transform inside <see cref="Post"/> (stage 2 on one helper), or a second helper does it too. Rows pass through
    /// in order and the plane ring has four slots, so a row is transformed while the previous one is emitted, and the
    /// output is identical to the single-threaded path.
    /// </summary>
    private sealed class StagedTransform<T> : IDisposable
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        private readonly JpegPipeline<T> _pipe;
        private readonly JpegCoefficientStore _store;
        private readonly bool _separate;
        private readonly object _gate = new();
        private readonly Thread? _transformThread;
        private readonly Thread _emitThread;
        private int _posted;
        private int _transformed;
        private int _emitted;
        private bool _finished;
        private bool _transformExited;
        private bool _stop;
        private Exception? _error;
        private long _fullBlocks;
        private long _reducedBlocks;

        public StagedTransform(JpegPipeline<T> pipe, JpegCoefficientStore store, bool separateTransform)
        {
            _pipe = pipe;
            _store = store;
            _separate = separateTransform;
            _transformExited = !separateTransform;
            _emitThread = new Thread(EmitWork) { IsBackground = true };
            _emitThread.Start();
            if (separateTransform)
            {
                _transformThread = new Thread(TransformWork) { IsBackground = true };
                _transformThread.Start();
            }
        }

        /// <summary>Called by the decoding thread once MCU row <paramref name="row"/> is complete (its coefficients are in the store).</summary>
        /// <returns><see langword="false"/> when decoding should stop (everything wanted was delivered, or a stage failed).</returns>
        /// <param name="row">The completed row.</param>
        /// <param name="lag">How many rows the transform stage may trail behind; the default of 1 suits a two-row coefficient ring.</param>
        public bool Post(int row, int lag = 1)
        {
            if (_separate)
            {
                lock (_gate)
                {
                    _posted = row + 1;
                    Monitor.PulseAll(_gate);

                    // The next row goes into the coefficient slots this row's predecessor used, so that one must be transformed first.
                    while (_transformed < row + 1 - lag && !_stop)
                    {
                        Monitor.Wait(_gate);
                    }

                    return !_stop;
                }
            }

            WaitForSlot(row);
            if (_stop)
            {
                return false;
            }

            try
            {
                _pipe.TransformRow(row, _store);
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }

            lock (_gate)
            {
                _transformed = row + 1;
                Monitor.PulseAll(_gate);
                return !_stop;
            }
        }

        /// <summary>Gets the number of leading rows the transform stage has finished.</summary>
        public int Transformed
        {
            get
            {
                lock (_gate)
                {
                    return _transformed;
                }
            }
        }

        /// <summary>Blocks until <paramref name="count"/> leading rows are transformed.</summary>
        /// <returns><see langword="false"/> when the stages stopped first.</returns>
        public bool WaitTransformed(int count)
        {
            lock (_gate)
            {
                while (_transformed < count && !_stop)
                {
                    Monitor.Wait(_gate);
                }

                return !_stop;
            }
        }

        /// <summary>Waits until every posted row is delivered and rethrows a failure from any stage.</summary>
        public void Finish()
        {
            lock (_gate)
            {
                _finished = true;
                Monitor.PulseAll(_gate);
            }

            Join();
            if (_error is { } e)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
            }
        }

        /// <summary>After the decoder failed: lets the stages finish the rows already handed over and reports whether that completed the request.</summary>
        public bool TryComplete(int rowsWanted)
        {
            lock (_gate)
            {
                _finished = true;
                Monitor.PulseAll(_gate);
            }

            Join();
            return _error is null && _pipe.RowsWritten == rowsWanted;
        }

        public void Dispose()
        {
            // Reached without Finish when the decoder threw: let the helpers leave, and wait so they no longer touch the buffers.
            lock (_gate)
            {
                _finished = true;
                _stop = true;
                Monitor.PulseAll(_gate);
            }

            Join();
        }

        private void Join()
        {
            _transformThread?.Join();
            _emitThread.Join();
            JpegDiagnostics.FullBlocks += _fullBlocks;
            JpegDiagnostics.ReducedBlocks += _reducedBlocks;
            _fullBlocks = 0;
            _reducedBlocks = 0;
        }

        // Transforming row k overwrites the slot of row k-4, which the emit steps for rows up to k-2 still read.
        private void WaitForSlot(int k)
        {
            lock (_gate)
            {
                while (_emitted < k - 1 && !_stop)
                {
                    Monitor.Wait(_gate);
                }
            }
        }

        private void Fail(Exception ex)
        {
            // Handed to the decoding thread, which rethrows it unchanged.
            lock (_gate)
            {
                _error ??= ex;
                _stop = true;
                Monitor.PulseAll(_gate);
            }
        }

        private void TransformWork()
        {
            try
            {
                int next = 0;
                while (true)
                {
                    lock (_gate)
                    {
                        while (_posted <= next && !_finished && !_stop)
                        {
                            Monitor.Wait(_gate);
                        }

                        if (_stop || _posted <= next)
                        {
                            break;
                        }
                    }

                    WaitForSlot(next);
                    if (_stop)
                    {
                        break;
                    }

                    _pipe.TransformRow(next, _store);
                    next++;
                    lock (_gate)
                    {
                        _transformed = next;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                _fullBlocks = JpegDiagnostics.FullBlocks;
                _reducedBlocks = JpegDiagnostics.ReducedBlocks;
                lock (_gate)
                {
                    _transformExited = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void EmitWork()
        {
            try
            {
                int next = 0;
                while (true)
                {
                    lock (_gate)
                    {
                        while (_transformed <= next && !_stop && !(_finished && _transformExited))
                        {
                            Monitor.Wait(_gate);
                        }

                        if (_stop || _transformed <= next)
                        {
                            return;
                        }
                    }

                    bool more = _pipe.EmitRow(next);
                    next++;
                    lock (_gate)
                    {
                        _emitted = next;
                        if (!more)
                        {
                            _stop = true;
                        }

                        Monitor.PulseAll(_gate);
                    }

                    if (!more)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }
    }
}
