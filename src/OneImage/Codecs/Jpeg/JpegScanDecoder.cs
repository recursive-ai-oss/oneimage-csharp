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
    private readonly JpegReader _reader;
    private readonly double _maxExpansion;
    private long _allocated;

    public JpegCoefficientStore(JpegFrame frame, bool ring, JpegReader reader, ImageLimits limits)
    {
        _components = frame.Components;
        _ring = ring;
        _reader = reader;
        _maxExpansion = limits.MaxExpansionRatio;
        _rows = new short[]?[_components.Length][];
        for (int i = 0; i < _components.Length; i++)
        {
            JpegComponent c = _components[i];
            _rows[i] = new short[ring ? c.V : c.BlockRows][];
            if (ring)
            {
                for (int r = 0; r < c.V; r++)
                {
                    _rows[i][r] = Rent(c.BlocksPerLine * 64);
                }
            }
        }
    }

    private static short[] Rent(int length)
    {
        short[] a = ArrayPool<short>.Shared.Rent(length);
        a.AsSpan(0, length).Clear();
        return a;
    }

    /// <summary>Gets the blocks of one block row of a component.</summary>
    public Span<short> Row(JpegComponent c, int blockRow)
    {
        int len = c.BlocksPerLine * 64;
        short[]?[] rows = _rows[c.Index];
        int slot = _ring ? blockRow % c.V : blockRow;
        short[]? a = rows[slot];
        if (a is null)
        {
            // Progressive data expands a lot, but not without bound: refuse rows that the bytes read so far cannot explain.
            _allocated += (long)len * sizeof(short);
            if (_allocated > (_maxExpansion * (_reader.BytesRead + 65536)) && !double.IsPositiveInfinity(_maxExpansion))
            {
                throw new ImageLimitException("JPEG coefficient data would expand beyond the allowed ratio of the compressed size.");
            }

            a = Rent(len);
            rows[slot] = a;
        }

        return a.AsSpan(0, len);
    }

    /// <summary>Gives a finished block row back (not for ring storage).</summary>
    public void Release(JpegComponent c, int blockRow)
    {
        if (_ring)
        {
            return;
        }

        short[]? a = _rows[c.Index][blockRow];
        if (a is not null)
        {
            ArrayPool<short>.Shared.Return(a);
            _rows[c.Index][blockRow] = null;
            _allocated -= (long)c.BlocksPerLine * 64 * sizeof(short);
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
                    ArrayPool<short>.Shared.Return(a);
                    comp[i] = null;
                }
            }
        }
    }
}

/// <summary>Decodes the entropy-coded data of one scan into coefficient blocks (T.81 F.2 and G.1.2).</summary>
internal sealed class JpegScanDecoder
{
    private enum Mode
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

    /// <summary>Gets or sets whether only DC coefficients are needed, so AC data is parsed but not stored.</summary>
    public bool DcOnly { get; set; }

    /// <summary>
    /// Decodes a whole scan. <paramref name="mcuRowDone"/> is called after every MCU row (a row of blocks for
    /// non-interleaved scans); returning false stops decoding.
    /// </summary>
    /// <returns><see langword="false"/> if stopped early by the callback.</returns>
    public bool Decode(JpegScan scan, Func<int, bool>? mcuRowDone, CancellationToken ct)
    {
        Mode mode = SelectMode(scan);
        JpegComponent[] comps = scan.Components;
        JpegHuffmanTable?[] dcT = new JpegHuffmanTable?[comps.Length];
        JpegHuffmanTable?[] acT = new JpegHuffmanTable?[comps.Length];
        for (int i = 0; i < comps.Length; i++)
        {
            comps[i].DcPred = 0;
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
            }
        }

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
        int ri = _t.RestartInterval;
        int untilRestart = ri;
        for (int my = 0; my < rows; my++)
        {
            ct.ThrowIfCancellationRequested();
            for (int mx = 0; mx < perRow; mx++)
            {
                if (ri > 0)
                {
                    if (untilRestart == 0)
                    {
                        _r.ProcessRestart();
                        for (int i = 0; i < comps.Length; i++)
                        {
                            comps[i].DcPred = 0;
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
                arith!.DecodeSequential(block, c, DcOnly);
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
        _r.DecodeBlockBaseline(block, ref c.DcPred, dc, ac, DcOnly);

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

    private void DecodeAcFirst(Span<short> block, JpegHuffmanTable ac, int ss, int se, int al)
    {
        if (_eobRun > 0)
        {
            _eobRun--;
            return;
        }

        JpegReader r = _r;
        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        for (int k = ss; k <= se; k++)
        {
            if (r.BitCount < 32)
            {
                r.Fill();
            }

            int rs = r.DecodeHuffman(ac);
            int s = rs & 15;
            int run = rs >> 4;
            if (s != 0)
            {
                k += run;
                if (k > 63)
                {
                    throw new ImageFormatException("JPEG AC coefficient index out of range.");
                }

                block[zz[k]] = (short)(r.ReceiveExtend(s) << al);
            }
            else if (run == 15)
            {
                k += 15;
            }
            else
            {
                _eobRun = 1 << run;
                if (run != 0)
                {
                    _eobRun += r.GetBits(run);
                }

                _eobRun--;
                break;
            }
        }
    }

    private void DecodeAcRefine(Span<short> block, JpegHuffmanTable ac, int ss, int se, int al)
    {
        JpegReader r = _r;
        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        int p1 = 1 << al;
        int m1 = -1 << al;
        int k = ss;
        if (_eobRun == 0)
        {
            for (; k <= se; k++)
            {
                if (r.BitCount < 32)
                {
                    r.Fill();
                }

                int rs = r.DecodeHuffman(ac);
                int s = rs & 15;
                int run = rs >> 4;
                if (s != 0)
                {
                    if (s != 1)
                    {
                        throw new ImageFormatException("Invalid JPEG progressive refinement.");
                    }

                    s = r.GetBit() != 0 ? p1 : m1;
                }
                else if (run != 15)
                {
                    _eobRun = 1 << run;
                    if (run != 0)
                    {
                        _eobRun += r.GetBits(run);
                    }

                    break;
                }

                // Skip over already-nonzero coefficients (refining them) and 'run' zero ones.
                do
                {
                    ref short coef = ref block[zz[k]];
                    if (coef != 0)
                    {
                        if (r.BitCount < 1)
                        {
                            r.Fill();
                        }

                        if (r.GetBit() != 0 && (coef & p1) == 0)
                        {
                            coef += (short)(coef >= 0 ? p1 : m1);
                        }
                    }
                    else if (--run < 0)
                    {
                        break;
                    }

                    k++;
                }
                while (k <= se);

                if (s != 0)
                {
                    if (k > se)
                    {
                        throw new ImageFormatException("JPEG AC coefficient index out of range.");
                    }

                    block[zz[k]] = (short)s;
                }
            }
        }

        if (_eobRun > 0)
        {
            for (; k <= se; k++)
            {
                ref short coef = ref block[zz[k]];
                if (coef != 0)
                {
                    if (r.BitCount < 1)
                    {
                        r.Fill();
                    }

                    if (r.GetBit() != 0 && (coef & p1) == 0)
                    {
                        coef += (short)(coef >= 0 ? p1 : m1);
                    }
                }
            }

            _eobRun--;
        }
    }
}
