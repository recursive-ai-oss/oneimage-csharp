using System.Runtime.CompilerServices;

namespace OneImage.Codecs.Jbig2;

/// <summary>
/// The MQ arithmetic decoder of T.88 annex E (the same coder as JBIG1 and JPEG 2000 uses), reading one
/// arithmetic-coded block. Past the end of the block, and at a marker, it supplies 1 bits as E.3.4 requires.
/// </summary>
internal sealed class Jbig2ArithmeticDecoder
{
    // Table E.1: Qe value, next index after an MPS, next index after an LPS, and whether an LPS switches the MPS sense.
    private static readonly ushort[] s_qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
        0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
        0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
        0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

    private static readonly byte[] s_nmps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    private static readonly byte[] s_nlps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    private static readonly byte[] s_switch =
    [
        1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0,
    ];

    // How many times the decoder may feed 1 bits at a marker or past the end before the data counts as truncated.
    // A correct stream needs only a couple of fill bytes after its last decision.
    private const int MaxFillBytes = 1 << 12;

    private readonly byte[] _data;
    private readonly int _end;
    private int _bp;
    private uint _c;
    private uint _a;
    private int _ct;
    private int _fill;

    /// <summary>Starts decoding <paramref name="data"/>[<paramref name="start"/>..<paramref name="end"/>) (INITDEC, E.3.5).</summary>
    public Jbig2ArithmeticDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _end = end;
        _bp = start;
        _c = (uint)At(_bp) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    private int At(int i) => i < _end ? _data[i] : 0xFF;

    private void ByteIn()
    {
        if (_bp >= _end || (At(_bp) == 0xFF && At(_bp + 1) > 0x8F))
        {
            if (++_fill > MaxFillBytes)
            {
                throw new ImageFormatException("JBIG2 arithmetic-coded data ended before the expected number of decisions.");
            }

            _c += 0xFF00;
            _ct = 8;
            return;
        }

        if (_data[_bp] == 0xFF)
        {
            _bp++;
            _c += (uint)At(_bp) << 9;
            _ct = 7;
        }
        else
        {
            _bp++;
            _c += (uint)At(_bp) << 8;
            _ct = 8;
        }
    }

    /// <summary>Decodes one decision with the adaptive state <paramref name="cx"/>[<paramref name="index"/>] (low bit: MPS, other bits: Qe index).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(byte[] cx, int index)
    {
        int st = cx[index];
        int i = st >> 1;
        int mps = st & 1;
        uint qe = s_qe[i];
        _a -= qe;
        int d;
        if ((_c >> 16) < qe)
        {
            // LPS_EXCHANGE (E.3.2, Figure E.17).
            if (_a < qe)
            {
                d = mps;
                i = s_nmps[i];
            }
            else
            {
                d = 1 - mps;
                if (s_switch[i] != 0)
                {
                    mps = 1 - mps;
                }

                i = s_nlps[i];
            }

            _a = qe;
            cx[index] = (byte)((i << 1) | mps);
            Renormalize();
            return d;
        }

        _c -= qe << 16;
        if ((_a & 0x8000) != 0)
        {
            return mps;
        }

        // MPS_EXCHANGE (Figure E.16).
        if (_a < qe)
        {
            d = 1 - mps;
            if (s_switch[i] != 0)
            {
                mps = 1 - mps;
            }

            i = s_nlps[i];
        }
        else
        {
            d = mps;
            i = s_nmps[i];
        }

        cx[index] = (byte)((i << 1) | mps);
        Renormalize();
        return d;
    }

    private void Renormalize()
    {
        do
        {
            if (_ct == 0)
            {
                ByteIn();
            }

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
    }
}

/// <summary>
/// Arithmetic integer decoding (T.88 annex A): a binary decision tree whose nodes are the 512 contexts of one
/// decoder instance. Each kind of value (IADH, IADW, ...) owns an instance, so their statistics stay separate.
/// </summary>
internal sealed class Jbig2IntegerDecoder
{
    private readonly byte[] _cx = new byte[512];

    /// <summary>
    /// Decodes one value (A.2). Returns false for the out-of-band value; otherwise <paramref name="value"/> is the result.
    /// </summary>
    /// <exception cref="ImageFormatException">The value does not fit in 32 bits.</exception>
    public bool Decode(Jbig2ArithmeticDecoder mq, out int value)
    {
        int prev = 1;
        int s = Bit(mq, ref prev);
        long v;
        if (Bit(mq, ref prev) == 0)
        {
            v = Bits(mq, ref prev, 2);
        }
        else if (Bit(mq, ref prev) == 0)
        {
            v = Bits(mq, ref prev, 4) + 4;
        }
        else if (Bit(mq, ref prev) == 0)
        {
            v = Bits(mq, ref prev, 6) + 20;
        }
        else if (Bit(mq, ref prev) == 0)
        {
            v = Bits(mq, ref prev, 8) + 84;
        }
        else if (Bit(mq, ref prev) == 0)
        {
            v = Bits(mq, ref prev, 12) + 340;
        }
        else
        {
            v = Bits(mq, ref prev, 32) + 4436;
        }

        if (s == 1)
        {
            if (v == 0)
            {
                value = 0;
                return false;
            }

            v = -v;
        }

        if (v is > int.MaxValue or < int.MinValue)
        {
            throw new ImageFormatException("A JBIG2 integer is outside the 32-bit range.");
        }

        value = (int)v;
        return true;
    }

    private int Bit(Jbig2ArithmeticDecoder mq, ref int prev)
    {
        int d = mq.Decode(_cx, prev);
        prev = prev < 256 ? (prev << 1) | d : (((prev << 1) | d) & 511) | 256;
        return d;
    }

    private long Bits(Jbig2ArithmeticDecoder mq, ref int prev, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++)
        {
            v = (v << 1) | (uint)Bit(mq, ref prev);
        }

        return v;
    }
}

/// <summary>The IAID procedure (T.88 A.3): a fixed-length symbol ID whose context is every bit decoded so far.</summary>
internal sealed class Jbig2SymbolIdDecoder
{
    private readonly byte[] _cx;
    private readonly int _length;

    /// <summary>Creates a decoder for <paramref name="codeLength"/>-bit IDs (SBSYMCODELEN).</summary>
    public Jbig2SymbolIdDecoder(int codeLength)
    {
        if (codeLength is < 0 or > 30)
        {
            throw new ImageFormatException("A JBIG2 symbol code length is out of range.");
        }

        _length = codeLength;
        _cx = new byte[1 << (codeLength + 1)];
    }

    /// <summary>Decodes one symbol ID.</summary>
    public int Decode(Jbig2ArithmeticDecoder mq)
    {
        int prev = 1;
        for (int i = 0; i < _length; i++)
        {
            prev = (prev << 1) | mq.Decode(_cx, prev);
        }

        return prev - (1 << _length);
    }
}

/// <summary>
/// The adaptive statistics that symbol dictionaries and text regions share: one integer decoder per value kind
/// plus the symbol ID decoder. A fresh instance is the "all statistics reset" state of T.88 E.3.7.
/// </summary>
internal sealed class Jbig2IntegerContexts
{
    /// <summary>Creates the contexts; <paramref name="symbolCodeLength"/> is SBSYMCODELEN.</summary>
    public Jbig2IntegerContexts(int symbolCodeLength)
    {
        Id = new Jbig2SymbolIdDecoder(symbolCodeLength);
    }

    /// <summary>IADH: height class delta heights.</summary>
    public Jbig2IntegerDecoder Dh { get; } = new();

    /// <summary>IADW: symbol delta widths.</summary>
    public Jbig2IntegerDecoder Dw { get; } = new();

    /// <summary>IAEX: export run lengths.</summary>
    public Jbig2IntegerDecoder Ex { get; } = new();

    /// <summary>IAAI: symbol instances in an aggregation.</summary>
    public Jbig2IntegerDecoder Ai { get; } = new();

    /// <summary>IADT: strip delta T.</summary>
    public Jbig2IntegerDecoder Dt { get; } = new();

    /// <summary>IAFS: first symbol S coordinate of a strip.</summary>
    public Jbig2IntegerDecoder Fs { get; } = new();

    /// <summary>IADS: later symbol S coordinates.</summary>
    public Jbig2IntegerDecoder Ds { get; } = new();

    /// <summary>IAIT: symbol T coordinate within a strip.</summary>
    public Jbig2IntegerDecoder It { get; } = new();

    /// <summary>IARI: the refinement flag.</summary>
    public Jbig2IntegerDecoder Ri { get; } = new();

    /// <summary>IARDW: refinement delta width.</summary>
    public Jbig2IntegerDecoder Rdw { get; } = new();

    /// <summary>IARDH: refinement delta height.</summary>
    public Jbig2IntegerDecoder Rdh { get; } = new();

    /// <summary>IARDX: refinement X offset.</summary>
    public Jbig2IntegerDecoder Rdx { get; } = new();

    /// <summary>IARDY: refinement Y offset.</summary>
    public Jbig2IntegerDecoder Rdy { get; } = new();

    /// <summary>IAID: symbol IDs.</summary>
    public Jbig2SymbolIdDecoder Id { get; }
}
