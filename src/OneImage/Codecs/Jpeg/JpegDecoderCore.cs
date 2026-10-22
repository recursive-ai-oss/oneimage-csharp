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

    private static void Run<T>(JpegReader reader, JpegHeaders h, JpegFrame f, JpegColorPlan plan, JpegTables tables, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken ct)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        using var pipe = new JpegPipeline<T>(f, plan, request.ScaleDenominator, request.Region, sink);
        bool dcOnly = pipe.DcOnly;
        JpegScan scan = JpegParser.ParseSos(reader, f);

        // A sequential image with a single scan over all components streams MCU row by MCU row; everything else
        // (progressive, or components split over several scans) needs all coefficients before the first pixel row.
        bool streaming = !f.Progressive && scan.Components.Length == f.Components.Length;
        bool overlapped = streaming && (long)f.Width * f.Height >= OverlapMinPixels && Environment.ProcessorCount > 1;
        using var store = new JpegCoefficientStore(f, streaming, reader, limits, overlapped ? 2 : 1);
        var decoder = new JpegScanDecoder(reader, f, tables, store) { DcOnly = dcOnly };
        using RowHandoff? handoff = overlapped ? new RowHandoff(row => pipe.AddMcuRow(row, store)) : null;
        Func<int, bool>? onRow = handoff is not null ? handoff.Post : streaming ? row => pipe.AddMcuRow(row, store) : null;
        bool more = true;
        while (more)
        {
            LatchQuantTables(scan, tables);
            if (f.Progressive && dcOnly && scan.Ss > 0)
            {
                // Only the DC image matters at 1/8 size, so skip the AC scans without decoding them.
                reader.SkipEntropyData();
            }
            else
            {
                decoder.Decode(scan, onRow, ct);
                handoff?.Finish();
            }

            if (streaming)
            {
                break;
            }

            more = NextScan(reader, h, f, tables, limits, out scan);
        }

        if (!streaming)
        {
            for (int r = 0; r < f.McusY; r++)
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

    private static bool NextScan(JpegReader reader, JpegHeaders h, JpegFrame f, JpegTables tables, ImageLimits limits, out JpegScan scan)
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

    private static void LatchQuantTables(JpegScan scan, JpegTables tables)
    {
        foreach (JpegComponent c in scan.Components)
        {
            c.Quant ??= tables.Quant[c.Tq] ?? throw new ImageFormatException("JPEG scan uses an undefined quantization table.");
        }
    }

    /// <summary>
    /// Runs the per-MCU-row transform (inverse DCT, upsampling, color conversion, delivery to the sink) on a second thread
    /// while the calling thread entropy-decodes the next MCU row. The coefficient store holds two MCU rows, so the decoder
    /// may run one row ahead; rows are still transformed and delivered in order, so output is identical to the
    /// single-threaded path.
    /// </summary>
    private sealed class RowHandoff : IDisposable
    {
        private readonly Func<int, bool> _consume;
        private readonly object _gate = new();
        private readonly Thread _thread;
        private int _posted;
        private int _consumed;
        private bool _finished;
        private bool _stop;
        private Exception? _error;
        private long _fullBlocks;
        private long _reducedBlocks;

        public RowHandoff(Func<int, bool> consume)
        {
            _consume = consume;
            _thread = new Thread(Work) { IsBackground = true };
            _thread.Start();
        }

        /// <summary>Called by the decoding thread after MCU row <paramref name="row"/> is in the store.</summary>
        /// <returns><see langword="false"/> when decoding should stop (everything wanted was delivered, or the consumer failed).</returns>
        public bool Post(int row)
        {
            lock (_gate)
            {
                _posted = row + 1;
                Monitor.PulseAll(_gate);

                // The next row goes into the slots the previous row used, so that one must be consumed first.
                while (_consumed < row && !_stop)
                {
                    Monitor.Wait(_gate);
                }

                return !_stop;
            }
        }

        /// <summary>Waits until every posted row is consumed and rethrows a failure from the consumer.</summary>
        public void Finish()
        {
            lock (_gate)
            {
                _finished = true;
                Monitor.PulseAll(_gate);
            }

            _thread.Join();
            JpegDiagnostics.FullBlocks += _fullBlocks;
            JpegDiagnostics.ReducedBlocks += _reducedBlocks;
            if (_error is { } e)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
            }
        }

        public void Dispose()
        {
            // Reached without Finish when the decoder threw: let the worker leave, and wait so it no longer touches the buffers.
            lock (_gate)
            {
                _finished = true;
                _stop = true;
                Monitor.PulseAll(_gate);
            }

            _thread.Join();
        }

        private void Work()
        {
            try
            {
                int next = 0;
                while (true)
                {
                    lock (_gate)
                    {
                        while (_posted <= next && !_finished)
                        {
                            Monitor.Wait(_gate);
                        }

                        if (_stop || _posted <= next)
                        {
                            return;
                        }
                    }

                    bool more = _consume(next);
                    next++;
                    lock (_gate)
                    {
                        _consumed = next;
                        if (!more)
                        {
                            _stop = true;
                        }

                        Monitor.PulseAll(_gate);
                        if (!more)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handed to the decoding thread, which rethrows it unchanged.
                lock (_gate)
                {
                    _error = ex;
                    _stop = true;
                    Monitor.PulseAll(_gate);
                }
            }
            finally
            {
                _fullBlocks = JpegDiagnostics.FullBlocks;
                _reducedBlocks = JpegDiagnostics.ReducedBlocks;
            }
        }
    }
}
