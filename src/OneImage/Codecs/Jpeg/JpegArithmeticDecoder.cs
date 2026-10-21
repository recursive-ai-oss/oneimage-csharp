namespace OneImage.Codecs.Jpeg;

/// <summary>
/// Arithmetic entropy decoding of DCT coefficients (T.81 Annex D, F.2.4 and G.1.3): the QM-coder with the
/// context models of the sequential and progressive processes.
/// </summary>
internal sealed class JpegArithmeticDecoder
{
    private readonly JpegReader _r;
    private readonly JpegTables _t;
    private readonly byte[][] _dcStats = new byte[4][];
    private readonly byte[][] _acStats = new byte[4][];
    private readonly byte[] _fixed = [JpegArithmeticTables.FixedState];
    private int[] _dcContext = [];
    private JpegComponent[] _comps = [];
    private long _c;
    private long _a;
    private int _ct;

    public JpegArithmeticDecoder(JpegReader reader, JpegTables tables, int precision)
    {
        _ = precision;
        _r = reader;
        _t = tables;
        for (int i = 0; i < 4; i++)
        {
            _dcStats[i] = new byte[64];
            _acStats[i] = new byte[256];
        }
    }

    public void StartScan(JpegComponent[] comps)
    {
        _comps = comps;
        _dcContext = new int[comps.Length];
        Restart();
    }

    /// <summary>Resets statistics, DC context and the coder (scan start and every restart).</summary>
    public void Restart()
    {
        for (int i = 0; i < 4; i++)
        {
            Array.Clear(_dcStats[i]);
            Array.Clear(_acStats[i]);
        }

        Array.Clear(_dcContext);
        _c = 0;
        _a = 0;
        _ct = -16;
        _fixed[0] = JpegArithmeticTables.FixedState;
    }

    private int Slot(JpegComponent c)
    {
        for (int i = 0; i < _comps.Length; i++)
        {
            if (_comps[i] == c)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Decodes one binary decision with the adaptive state <paramref name="st"/> (low 7 bits index, bit 7 MPS).</summary>
    private int Decode(ref byte st)
    {
        // Renormalize lazily, before the decision: this also performs the initial two-byte load.
        while (_a < 0x8000)
        {
            if (--_ct < 0)
            {
                _c = (_c << 8) | (uint)_r.NextEntropyByte();
                if ((_ct += 8) < 0 && ++_ct == 0)
                {
                    _a = 0x8000;
                }
            }

            _a <<= 1;
        }

        int sv = st;
        int index = sv & 0x7F;
        int mps = sv >> 7;
        int qe = JpegArithmeticTables.Qe(index);
        long a = _a - qe;
        long temp = a << _ct;
        int d;
        if (_c >= temp)
        {
            // The offset lies in the LPS sub-interval.
            _c -= temp;
            if (a < qe)
            {
                // Conditional exchange: the larger sub-interval is the MPS one after all.
                d = mps;
                st = (byte)((mps << 7) | JpegArithmeticTables.NextMps(index));
            }
            else
            {
                d = mps ^ 1;
                int next = JpegArithmeticTables.NextLps(index);
                int newMps = JpegArithmeticTables.SwitchMps(index) ? mps ^ 1 : mps;
                st = (byte)((newMps << 7) | next);
            }

            _a = qe;
        }
        else
        {
            _a = a;
            if (a < 0x8000)
            {
                if (a < qe)
                {
                    // Conditional exchange on the MPS path.
                    d = mps ^ 1;
                    int next = JpegArithmeticTables.NextLps(index);
                    int newMps = JpegArithmeticTables.SwitchMps(index) ? mps ^ 1 : mps;
                    st = (byte)((newMps << 7) | next);
                }
                else
                {
                    d = mps;
                    st = (byte)((mps << 7) | JpegArithmeticTables.NextMps(index));
                }
            }
            else
            {
                d = mps;
            }
        }

        return d;
    }

    /// <summary>Decodes a DC difference with the contexts of Table F.4 and updates the running DC value.</summary>
    private int DecodeDc(JpegComponent c)
    {
        int slot = Slot(c);
        byte[] stats = _dcStats[c.DcTable];
        int s0 = _dcContext[slot];
        if (Decode(ref stats[s0]) == 0)
        {
            _dcContext[slot] = 0;
            return c.DcPred;
        }

        int sign = Decode(ref stats[s0 + 1]);
        int st = s0 + 2 + sign;
        int m = Decode(ref stats[st]);
        if (m != 0)
        {
            st = 20;
            while (Decode(ref stats[st]) != 0)
            {
                if ((m <<= 1) == 0x8000)
                {
                    throw new ImageFormatException("Corrupt arithmetic-coded JPEG DC value.");
                }

                st++;
            }
        }

        int l = _t.DcL[c.DcTable];
        int u = _t.DcU[c.DcTable];
        if (m < (1 << l >> 1))
        {
            _dcContext[slot] = 0;
        }
        else if (m > (1 << u >> 1))
        {
            _dcContext[slot] = 12 + (sign * 4);
        }
        else
        {
            _dcContext[slot] = 4 + (sign * 4);
        }

        int v = m;
        st += 14;
        while ((m >>= 1) != 0)
        {
            if (Decode(ref stats[st]) != 0)
            {
                v |= m;
            }
        }

        v++;
        c.DcPred += sign != 0 ? -v : v;
        return c.DcPred;
    }

    /// <summary>Decodes a nonzero AC coefficient value at zigzag index <paramref name="k"/> whose context base is <paramref name="st"/> (the S0 bin + 1).</summary>
    private int DecodeAcValue(byte[] stats, int st, int k, int kx)
    {
        int sign = Decode(ref _fixed[0]);
        st += 2 - 1;
        int m = Decode(ref stats[st]);
        if (m != 0 && Decode(ref stats[st]) != 0)
        {
            m <<= 1;
            st = k <= kx ? 189 : 217;
            while (Decode(ref stats[st]) != 0)
            {
                if ((m <<= 1) == 0x8000)
                {
                    throw new ImageFormatException("Corrupt arithmetic-coded JPEG AC value.");
                }

                st++;
            }
        }

        int v = m;
        st += 14;
        while ((m >>= 1) != 0)
        {
            if (Decode(ref stats[st]) != 0)
            {
                v |= m;
            }
        }

        v++;
        return sign != 0 ? -v : v;
    }

    public void DecodeSequential(Span<short> block, JpegComponent c, bool dcOnly)
    {
        _ = dcOnly;
        block.Clear();
        block[0] = (short)DecodeDc(c);
        DecodeAcRange(block, c, 1, 63, 0);
    }

    public void DecodeDcFirst(Span<short> block, JpegComponent c, int al) => block[0] = (short)(DecodeDc(c) << al);

    public void DecodeDcRefine(Span<short> block, int al)
    {
        if (Decode(ref _fixed[0]) != 0)
        {
            block[0] |= (short)(1 << al);
        }
    }

    public void DecodeAcFirst(Span<short> block, JpegComponent c, int ss, int se, int al) => DecodeAcRange(block, c, ss, se, al);

    private void DecodeAcRange(Span<short> block, JpegComponent c, int ss, int se, int al)
    {
        byte[] stats = _acStats[c.AcTable];
        int kx = _t.AcKx[c.AcTable];
        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        for (int k = ss; k <= se; k++)
        {
            int st = 3 * (k - 1);
            if (Decode(ref stats[st]) != 0)
            {
                break;
            }

            while (Decode(ref stats[st + 1]) == 0)
            {
                st += 3;
                k++;
                if (k > se)
                {
                    throw new ImageFormatException("Corrupt arithmetic-coded JPEG AC run.");
                }
            }

            int v = DecodeAcValue(stats, st + 1, k, kx);
            block[zz[k]] = (short)(v << al);
        }
    }

    public void DecodeAcRefine(Span<short> block, JpegComponent c, int ss, int se, int al)
    {
        byte[] stats = _acStats[c.AcTable];
        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        int p1 = 1 << al;
        int m1 = -1 << al;
        int kex = se;
        while (kex > 0 && block[zz[kex]] == 0)
        {
            kex--;
        }

        for (int k = ss; k <= se; k++)
        {
            int st = 3 * (k - 1);
            if (k > kex && Decode(ref stats[st]) != 0)
            {
                break;
            }

            while (true)
            {
                ref short coef = ref block[zz[k]];
                if (coef != 0)
                {
                    if (Decode(ref stats[st + 2]) != 0)
                    {
                        coef += (short)(coef < 0 ? m1 : p1);
                    }

                    break;
                }

                if (Decode(ref stats[st + 1]) != 0)
                {
                    coef = (short)(Decode(ref _fixed[0]) != 0 ? m1 : p1);
                    break;
                }

                st += 3;
                k++;
                if (k > se)
                {
                    throw new ImageFormatException("Corrupt arithmetic-coded JPEG refinement.");
                }
            }
        }
    }
}
