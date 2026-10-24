using System.Buffers;
using System.Runtime.CompilerServices;

namespace OneImage.Codecs.Jpeg;

/// <summary>
/// Holds quantized coefficients as rows of blocks. A sequential single-scan image only ever needs one MCU row
/// (a ring of V block rows per component); progressive and multi-scan images keep every row, allocated lazily
/// as scans reach it so that a header claiming a huge image costs nothing until data backs it up.
/// </summary>
internal sealed class JpegCoefficientStore : IDisposable
{
    private readonly JpegComponent[] _components;
    private readonly short[]?[][] _rows;
    private readonly bool _ring;
    private readonly int _sets;
    private readonly JpegReader _reader;
    private readonly double _maxExpansion;
    private long _allocated;
    private readonly object _allocGate = new();

    /// <param name="frame">The frame whose components are stored.</param>
    /// <param name="ring">Whether to keep only the rows of the current MCU row (sequential single-scan images).</param>
    /// <param name="reader">The reader, whose byte count bounds lazily allocated rows.</param>
    /// <param name="limits">Limits applied to expansion.</param>
    /// <param name="sets">With a ring, how many MCU rows are held at once: 2 lets one thread decode the next MCU row while another transforms the previous one.</param>
    public JpegCoefficientStore(JpegFrame frame, bool ring, JpegReader reader, ImageLimits limits, int sets = 1)
    {
        _sets = ring ? sets : 1;
        _components = frame.Components;
        _ring = ring;
        _reader = reader;
        _maxExpansion = limits.MaxExpansionRatio;
        _rows = new short[]?[_components.Length][];
        for (int i = 0; i < _components.Length; i++)
        {
            JpegComponent c = _components[i];
            _rows[i] = new short[ring ? c.V * _sets : c.BlockRows][];
            if (ring)
            {
                for (int r = 0; r < c.V * _sets; r++)
                {
                    _rows[i][r] = Rent(c.BlocksPerLine * 64);
                }
            }
        }
    }

    private static short[] Rent(int length)
    {
        short[] a = Pools<short>.Pool.Rent(length);
        a.AsSpan(0, length).Clear();
        return a;
    }

    /// <summary>Gets the blocks of one block row of a component. Safe to call from several threads (scans decoded side by side).</summary>
    public Span<short> Row(JpegComponent c, int blockRow)
    {
        int len = c.BlocksPerLine * 64;
        short[]?[] rows = _rows[c.Index];
        int slot = _ring ? blockRow % (c.V * _sets) : blockRow;
        short[]? a = Volatile.Read(ref rows[slot]);
        if (a is null)
        {
            a = AllocateRow(rows, slot, len);
        }

        return a.AsSpan(0, len);
    }

    private short[] AllocateRow(short[]?[] rows, int slot, int len)
    {
        lock (_allocGate)
        {
            if (rows[slot] is { } existing)
            {
                return existing;
            }

            // Progressive data expands a lot, but not without bound: refuse rows that the bytes read so far cannot explain.
            _allocated += (long)len * sizeof(short);
            if (_allocated > (_maxExpansion * (_reader.BytesRead + 65536)) && !double.IsPositiveInfinity(_maxExpansion))
            {
                throw new ImageLimitException("JPEG coefficient data would expand beyond the allowed ratio of the compressed size.");
            }

            short[] a = Rent(len);
            Volatile.Write(ref rows[slot], a);
            return a;
        }
    }

    /// <summary>Gives a finished block row back (not for ring storage).</summary>
    public void Release(JpegComponent c, int blockRow)
    {
        if (_ring)
        {
            return;
        }

        lock (_allocGate)
        {
            short[]? a = _rows[c.Index][blockRow];
            if (a is not null)
            {
                Pools<short>.Pool.Return(a);
                _rows[c.Index][blockRow] = null;
                _allocated -= (long)c.BlocksPerLine * 64 * sizeof(short);
            }
        }
    }

    public void Dispose()
    {
        foreach (short[]?[] comp in _rows)
        {
            for (int i = 0; i < comp.Length; i++)
            {
                if (comp[i] is { } a)
                {
                    Pools<short>.Pool.Return(a);
                    comp[i] = null;
                }
            }
        }
    }
}

/// <summary>Decodes the entropy-coded data of one scan into coefficient blocks (T.81 F.2 and G.1.2).</summary>
internal sealed partial class JpegScanDecoder
{
    internal enum Mode
    {
        Baseline,
        DcFirst,
        DcRefine,
        AcFirst,
        AcRefine,
        ArithBaseline,
        ArithDcFirst,
        ArithDcRefine,
        ArithAcFirst,
        ArithAcRefine,
    }

    // Below this many pixels the 12-bit lookahead table (built per AC table) is not worth its construction.
    internal static int LookMinPixels = 1 << 18;

    private readonly JpegReader _r;
    private readonly JpegFrame _f;
    private readonly JpegTables _t;
    private readonly JpegCoefficientStore _store;
    private int _eobRun;

    public JpegScanDecoder(JpegReader reader, JpegFrame frame, JpegTables tables, JpegCoefficientStore store)
    {
        _r = reader;
        _f = frame;
        _t = tables;
        _store = store;
    }

    /// <summary>
    /// Gets or sets, per component index, whether only the DC coefficient of that component is needed, so its AC data is
    /// parsed but not stored. <see langword="null"/> means every component needs its AC coefficients.
    /// </summary>
    public bool[]? DcOnlyComponents { get; set; }

    /// <summary>A scan with its tables resolved, so it can be decoded later, while the shared component selections and tables describe other scans.</summary>
    internal sealed class ScanPlan
    {
        public required Mode Mode;
        public required JpegScan Scan;
        public required JpegComponent[] Comps;
        public required JpegHuffmanTable?[] DcTables;
        public required JpegHuffmanTable?[] AcTables;
        public required int RestartInterval;
    }

    /// <summary>
    /// Decodes a whole scan. <paramref name="mcuRowDone"/> is called after every MCU row (a row of blocks for
    /// non-interleaved scans); returning false stops decoding.
    /// </summary>
    /// <returns><see langword="false"/> if stopped early by the callback.</returns>
    public bool Decode(JpegScan scan, Func<int, bool>? mcuRowDone, CancellationToken ct) => Run(Prepare(scan), mcuRowDone, null, ct);

    /// <summary>Resolves the tables and restart interval a scan is decoded with, as they are right now (T.81 B.2.3: a scan header selects tables by index, and tables may be redefined between scans).</summary>
    public ScanPlan Prepare(JpegScan scan)
    {
        Mode mode = PrepareTables(scan, out JpegComponent[] comps, out JpegHuffmanTable?[] dcT, out JpegHuffmanTable?[] acT);
        return new ScanPlan { Mode = mode, Scan = scan, Comps = comps, DcTables = dcT, AcTables = acT, RestartInterval = _t.RestartInterval };
    }

    // Only these modes carry a DC predictor from block to block; progressive AC and refinement scans never touch it, which lets scans of one
    // component be decoded side by side without sharing it.
    private static bool UsesDcPredictor(Mode mode) => mode is Mode.Baseline or Mode.DcFirst || mode >= Mode.ArithBaseline;

    /// <summary>
    /// Decodes a prepared scan.
    /// </summary>
    /// <param name="plan">From <see cref="Prepare"/>.</param>
    /// <param name="mcuRowDone">Called after every MCU row (a row of blocks for non-interleaved scans); returning false stops decoding.</param>
    /// <param name="rowGate">Called before every row with its index; returning false stops decoding. Lets a scan wait for the scans it refines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="false"/> if stopped early by a callback.</returns>
    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public bool Run(ScanPlan plan, Func<int, bool>? mcuRowDone, Func<int, bool>? rowGate, CancellationToken ct)
    {
        Mode mode = plan.Mode;
        JpegScan scan = plan.Scan;
        JpegComponent[] comps = plan.Comps;
        JpegHuffmanTable?[] dcT = plan.DcTables;
        JpegHuffmanTable?[] acT = plan.AcTables;
        bool usesPred = UsesDcPredictor(mode);

        JpegArithmeticDecoder? arith = null;
        if (mode >= Mode.ArithBaseline)
        {
            arith = new JpegArithmeticDecoder(_r, _t, _f.Precision);
            arith.StartScan(comps);
        }

        _r.ResetBits();
        _r.ClearPendingMarker();
        _eobRun = 0;
        bool interleaved = comps.Length > 1;
        int perRow = interleaved ? _f.McusX : comps[0].WidthInBlocks;
        int rows = interleaved ? _f.McusY : comps[0].HeightInBlocks;
        int ri = plan.RestartInterval;
        int untilRestart = ri;
        for (int my = 0; my < rows; my++)
        {
            ct.ThrowIfCancellationRequested();
            if (rowGate is not null && !rowGate(my))
            {
                return false;
            }

            for (int mx = 0; mx < perRow; mx++)
            {
                if (ri > 0)
                {
                    if (untilRestart == 0)
                    {
                        _r.ProcessRestart();
                        if (usesPred)
                        {
                            for (int i = 0; i < comps.Length; i++)
                            {
                                comps[i].DcPred = 0;
                            }
                        }

                        _eobRun = 0;
                        arith?.Restart();
                        untilRestart = ri;
                    }

                    untilRestart--;
                }

                if (interleaved)
                {
                    for (int ci = 0; ci < comps.Length; ci++)
                    {
                        JpegComponent c = comps[ci];
                        for (int by = 0; by < c.V; by++)
                        {
                            Span<short> row = _store.Row(c, (my * c.V) + by);
                            for (int bx = 0; bx < c.H; bx++)
                            {
                                DecodeBlock(mode, scan, row.Slice(((mx * c.H) + bx) * 64, 64), c, dcT[ci], acT[ci], arith);
                            }
                        }
                    }
                }
                else
                {
                    JpegComponent c = comps[0];
                    DecodeBlock(mode, scan, _store.Row(c, my).Slice(mx * 64, 64), c, dcT[0], acT[0], arith);
                }

                if (_r.Overrun)
                {
                    throw new ImageFormatException("JPEG scan data ended early.");
                }
            }

            if (mcuRowDone is not null && !mcuRowDone(my))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Selects the scan's mode, resets the DC predictors and resolves its Huffman tables (building the AC lookahead tables for big images).</summary>
    private Mode PrepareTables(JpegScan scan, out JpegComponent[] comps, out JpegHuffmanTable?[] dcT, out JpegHuffmanTable?[] acT)
    {
        Mode mode = SelectMode(scan);
        comps = scan.Components;
        dcT = new JpegHuffmanTable?[comps.Length];
        acT = new JpegHuffmanTable?[comps.Length];
        for (int i = 0; i < comps.Length; i++)
        {
            if (UsesDcPredictor(mode))
            {
                comps[i].DcPred = 0;
            }

            if (mode < Mode.ArithBaseline)
            {
                bool needDc = mode is Mode.Baseline or Mode.DcFirst;
                bool needAc = mode is Mode.Baseline or Mode.AcFirst or Mode.AcRefine;
                if (needDc)
                {
                    dcT[i] = _t.Dc[comps[i].DcTable] ?? throw new ImageFormatException("JPEG scan uses an undefined DC Huffman table.");
                }

                if (needAc)
                {
                    acT[i] = _t.Ac[comps[i].AcTable] ?? throw new ImageFormatException("JPEG scan uses an undefined AC Huffman table.");
                }

                if (mode is Mode.Baseline or Mode.AcFirst or Mode.AcRefine && acT[i] is { } acTable && (long)_f.Width * _f.Height >= LookMinPixels && !(DcOnlyComponents is { } d && d[comps[i].Index]))
                {
                    // Build the lookahead table up front; for small images it costs more than it saves.
                    _ = acTable.Look;
                }
            }
        }

        return mode;
    }

    private Mode SelectMode(JpegScan s)
    {
        int arith = _f.Arithmetic ? (int)Mode.ArithBaseline : 0;
        if (!_f.Progressive)
        {
            return (Mode)arith;
        }

        if (s.Ss == 0)
        {
            return (Mode)(arith + (s.Ah == 0 ? (int)Mode.DcFirst : (int)Mode.DcRefine));
        }

        return (Mode)(arith + (s.Ah == 0 ? (int)Mode.AcFirst : (int)Mode.AcRefine));
    }

    private void DecodeBlock(Mode mode, JpegScan s, Span<short> block, JpegComponent c, JpegHuffmanTable? dc, JpegHuffmanTable? ac, JpegArithmeticDecoder? arith)
    {
        switch (mode)
        {
            case Mode.Baseline:
                DecodeBaseline(block, c, dc!, ac!);
                break;
            case Mode.DcFirst:
                DecodeDcFirst(block, c, dc!, s.Al);
                break;
            case Mode.DcRefine:
                if (_r.BitCount < 1)
                {
                    _r.Fill();
                }

                if (_r.GetBit() != 0)
                {
                    block[0] |= (short)(1 << s.Al);
                }

                break;
            case Mode.AcFirst:
                DecodeAcFirst(block, ac!, s.Ss, s.Se, s.Al);
                break;
            case Mode.AcRefine:
                DecodeAcRefine(block, ac!, s.Ss, s.Se, s.Al);
                break;
            case Mode.ArithBaseline:
                arith!.DecodeSequential(block, c, DcOnlyComponents is { } m1 && m1[c.Index]);
                break;
            case Mode.ArithDcFirst:
                arith!.DecodeDcFirst(block, c, s.Al);
                break;
            case Mode.ArithDcRefine:
                arith!.DecodeDcRefine(block, s.Al);
                break;
            case Mode.ArithAcFirst:
                arith!.DecodeAcFirst(block, c, s.Ss, s.Se, s.Al);
                break;
            default:
                arith!.DecodeAcRefine(block, c, s.Ss, s.Se, s.Al);
                break;
        }
    }

    private void DecodeBaseline(Span<short> block, JpegComponent c, JpegHuffmanTable dc, JpegHuffmanTable ac) =>
        _r.DecodeBlockBaseline(block, ref c.DcPred, dc, ac, DcOnlyComponents is { } m && m[c.Index]);

    private void DecodeDcFirst(Span<short> block, JpegComponent c, JpegHuffmanTable dc, int al)
    {
        JpegReader r = _r;
        if (r.BitCount < 32)
        {
            r.Fill();
        }

        int t = r.DecodeHuffman(dc);
        if (t > 15)
        {
            throw new ImageFormatException("Invalid JPEG DC coefficient size.");
        }

        if (t != 0)
        {
            c.DcPred += r.ReceiveExtend(t);
        }

        block[0] = (short)(c.DcPred << al);
    }

    private void DecodeAcFirst(Span<short> block, JpegHuffmanTable ac, int ss, int se, int al) =>
        _r.DecodeAcFirst(block, ac, ss, se, al, ref _eobRun);

    private void DecodeAcRefine(Span<short> block, JpegHuffmanTable ac, int ss, int se, int al) =>
        _r.DecodeAcRefine(block, ac, ss, se, al, ref _eobRun);
}
