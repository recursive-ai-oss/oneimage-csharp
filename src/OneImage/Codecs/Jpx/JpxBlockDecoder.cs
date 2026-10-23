using System.Runtime.CompilerServices;

namespace OneImage.Codecs.Jpx;

/// <summary>One codeword segment of a code-block: where its bytes are in the block's data buffer and how many coding passes it holds.</summary>
internal readonly record struct JpxSegment(int Offset, int Length, int Passes);

/// <summary>
/// The code-block decoder: the MQ arithmetic decoder (T.800 Annex C) and the three-pass bit-plane model (Annex D), including the
/// selective bypass, context reset, termination on every pass, vertically causal and segmentation-symbol styles.
/// </summary>
/// <remarks>
/// <para>Output is the coefficient magnitudes in half units (so the reconstruction point of a truncated coefficient, one half
/// of the last decoded bit-plane, needs no extra state), with the sign applied. Safe code throughout; one instance decodes one
/// block at a time and is reused.</para>
/// </remarks>
internal sealed class JpxBlockDecoder
{
    private const int SigN = 0x001;
    private const int SigS = 0x002;
    private const int SigW = 0x004;
    private const int SigE = 0x008;
    private const int SigNW = 0x010;
    private const int SigNE = 0x020;
    private const int SigSW = 0x040;
    private const int SigSE = 0x080;
    private const int SgnN = 0x100;
    private const int SgnS = 0x200;
    private const int SgnW = 0x400;
    private const int SgnE = 0x800;
    private const int Sig = 0x1000;
    private const int Visited = 0x2000;
    private const int Refined = 0x4000;
    private const int NeighborMask = 0xFF;

    private const int CtxRun = 17;
    private const int CtxUniform = 18;

    // T.800 Table C.2: Qe values and state transitions.
    private static readonly ushort[] Qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
        0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
        0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
        0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

    private static readonly byte[] Nmps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    private static readonly byte[] Nlps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    private static readonly bool[] Switch =
    [
        true, false, false, false, false, false, true, false, false, false, false, false, false, false, true, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false,
    ];

    // Context label per neighbour-significance pattern (T.800 Table D.1) for the three sub-band classes.
    private static readonly byte[] SigCtxLlLh = BuildSigContexts(0);
    private static readonly byte[] SigCtxHl = BuildSigContexts(1);
    private static readonly byte[] SigCtxHh = BuildSigContexts(2);

    // Sign context (Table D.3) in bits 0..3 and the XOR bit in bit 7, indexed by the N,S,W,E significance bits and their signs.
    private static readonly byte[] SignCtx = BuildSignContexts();

    private readonly byte[] _cx = new byte[19];
    private ushort[] _flags = [];
    private int[] _mag = [];
    private int[] _result = [];

    // MQ decoder registers.
    private byte[] _buf = [];
    private int _bp;
    private int _end;
    private uint _c;
    private uint _a;
    private int _ct;

    // Raw (bypass) bit reader.
    private int _rawByte;
    private int _rawBits;

    private int _w;
    private int _h;
    private int _stride;
    private bool _causal;

    /// <summary>The decoded coefficients (sign applied, half units), row-major <c>w*h</c>, valid after <see cref="Decode"/>.</summary>
    public ReadOnlySpan<int> Result => _result.AsSpan(0, _w * _h);

    /// <summary>The number of coding passes the last call processed.</summary>
    public int PassesDecoded { get; private set; }

    /// <summary>Decodes one code-block.</summary>
    /// <param name="data">The block's data: its codeword segments back to back.</param>
    /// <param name="segments">The codeword segments, in order.</param>
    /// <param name="w">Block width (1..1024).</param>
    /// <param name="h">Block height; <c>w*h</c> is at most 4096.</param>
    /// <param name="bitPlanes">The number of magnitude bit-planes that carry data (Mb - P, plus any ROI shift); at most 30.</param>
    /// <param name="orient">Sub-band: 0 LL, 1 HL, 2 LH, 3 HH.</param>
    /// <param name="style">The code-block style bits of COD/COC.</param>
    public void Decode(byte[] data, ReadOnlySpan<JpxSegment> segments, int w, int h, int bitPlanes, int orient, int style)
    {
        _w = w;
        _h = h;
        _stride = w + 2;
        int n = w * h;
        int flagCount = (w + 2) * (h + 2);
        if (_flags.Length < flagCount)
        {
            _flags = new ushort[flagCount];
        }
        else
        {
            Array.Clear(_flags, 0, flagCount);
        }

        if (_mag.Length < n)
        {
            _mag = new int[n];
            _result = new int[n];
        }
        else
        {
            Array.Clear(_mag, 0, n);
        }

        _causal = (style & 0x08) != 0;
        bool bypass = (style & 0x01) != 0;
        bool reset = (style & 0x02) != 0;
        bool segSymbols = (style & 0x20) != 0;
        byte[] sigCtx = orient == 1 ? SigCtxHl : orient == 3 ? SigCtxHh : SigCtxLlLh;

        ResetContexts();
        _buf = data;
        int pass = 0;
        int planeBase = bitPlanes - 1;
        PassesDecoded = 0;
        foreach (JpxSegment seg in segments)
        {
            if (seg.Passes <= 0)
            {
                continue;
            }

            int firstType = (pass + 2) % 3;
            bool raw = bypass && pass >= 10 && firstType != 2;
            if (raw)
            {
                RawInit(seg.Offset, seg.Offset + seg.Length);
            }
            else
            {
                MqInit(seg.Offset, seg.Offset + seg.Length);
            }

            for (int i = 0; i < seg.Passes; i++, pass++)
            {
                int bp = planeBase - ((pass + 2) / 3);
                if (bp < 0)
                {
                    goto done;
                }

                switch ((pass + 2) % 3)
                {
                    case 0:
                        SigProp(bp, raw, sigCtx);
                        break;
                    case 1:
                        MagRef(bp, raw);
                        break;
                    default:
                        Cleanup(bp, sigCtx);
                        if (segSymbols)
                        {
                            MqBit(CtxUniform);
                            MqBit(CtxUniform);
                            MqBit(CtxUniform);
                            MqBit(CtxUniform);
                        }

                        break;
                }

                PassesDecoded++;
                if (reset)
                {
                    ResetContexts();
                }
            }
        }

    done:
        // Apply the sign: the magnitude array holds half-unit magnitudes.
        ushort[] f = _flags;
        int[] mag = _mag;
        int[] res = _result;
        for (int y = 0; y < h; y++)
        {
            int fi = (y + 1) * _stride + 1;
            int oi = y * w;
            for (int x = 0; x < w; x++)
            {
                int m = mag[oi + x];
                res[oi + x] = (f[fi + x] & 0x8000) != 0 ? -m : m;
            }
        }
    }

    private void ResetContexts()
    {
        Array.Clear(_cx);
        _cx[0] = 4 << 1;
        _cx[CtxRun] = 3 << 1;
        _cx[CtxUniform] = 46 << 1;
    }

    // ---- MQ decoder (T.800 C.3) ----

    private void MqInit(int start, int end)
    {
        _bp = start;
        _end = end;
        _c = (uint)(_bp < _end ? _buf[_bp] : 0xFF) << 16;
        MqByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MqByteIn()
    {
        int b = _bp < _end ? _buf[_bp] : 0xFF;
        if (b == 0xFF)
        {
            int b1 = _bp + 1 < _end ? _buf[_bp + 1] : 0xFF;
            if (b1 > 0x8F)
            {
                // A marker (or the end of the segment): feed 1 bits.
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _bp++;
                _c += (uint)b1 << 9;
                _ct = 7;
            }
        }
        else
        {
            _bp++;
            int b1 = _bp < _end ? _buf[_bp] : 0xFF;
            _c += (uint)b1 << 8;
            _ct = 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MqBit(int ctx)
    {
        int st = _cx[ctx];
        int idx = st >> 1;
        int mps = st & 1;
        uint qe = Qe[idx];
        _a -= qe;
        int d;
        if ((_c >> 16) < qe)
        {
            // LPS_EXCHANGE
            if (_a < qe)
            {
                d = mps;
                _cx[ctx] = (byte)((Nmps[idx] << 1) | mps);
            }
            else
            {
                d = 1 - mps;
                if (Switch[idx])
                {
                    mps = 1 - mps;
                }

                _cx[ctx] = (byte)((Nlps[idx] << 1) | mps);
            }

            _a = qe;
            MqRenorm();
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) == 0)
            {
                // MPS_EXCHANGE
                if (_a < qe)
                {
                    d = 1 - mps;
                    if (Switch[idx])
                    {
                        mps = 1 - mps;
                    }

                    _cx[ctx] = (byte)((Nlps[idx] << 1) | mps);
                }
                else
                {
                    d = mps;
                    _cx[ctx] = (byte)((Nmps[idx] << 1) | mps);
                }

                MqRenorm();
            }
            else
            {
                d = mps;
            }
        }

        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MqRenorm()
    {
        do
        {
            if (_ct == 0)
            {
                MqByteIn();
            }

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
    }

    // ---- Raw bits for the selective arithmetic coding bypass (T.800 D.6) ----

    private void RawInit(int start, int end)
    {
        _bp = start;
        _end = end;
        _rawByte = 0;
        _rawBits = 0;
    }

    private int RawBit()
    {
        if (_rawBits == 0)
        {
            // After an 0xFF byte the next byte carries only seven bits (its top bit is a stuffed zero).
            int prev = _rawByte;
            _rawByte = _bp < _end ? _buf[_bp++] : 0xFF; // D.4.1: missing bytes read as 0xFF
            _rawBits = prev == 0xFF ? 7 : 8;
        }

        _rawBits--;
        return (_rawByte >> _rawBits) & 1;
    }

    // ---- Coding passes (T.800 D.3) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSignificant(int fi, int neg, int y)
    {
        ushort[] f = _flags;
        int s = _stride;
        int ns = neg != 0 ? 1 : 0;
        f[fi] |= (ushort)(Sig | (ns << 15));
        f[fi - 1] |= (ushort)(SigE | (ns * SgnE));
        f[fi + 1] |= (ushort)(SigW | (ns * SgnW));
        f[fi + s] |= (ushort)(SigN | (ns * SgnN));
        f[fi + s - 1] |= SigNE;
        f[fi + s + 1] |= SigNW;

        // Vertically causal: the stripe above must not see samples of the stripe below.
        if (!_causal || (y & 3) != 0)
        {
            f[fi - s] |= (ushort)(SigS | (ns * SgnS));
            f[fi - s - 1] |= SigSE;
            f[fi - s + 1] |= SigSW;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeSign(int f)
    {
        int idx = (f & 0xF) | ((f >> 4) & 0xF0);
        int e = SignCtx[idx];
        return MqBit(e & 0x0F) ^ (e >> 7);
    }

    private void SigProp(int bp, bool raw, byte[] sigCtx)
    {
        ushort[] flags = _flags;
        int[] mag = _mag;
        int w = _w;
        int h = _h;
        int s = _stride;
        for (int y0 = 0; y0 < h; y0 += 4)
        {
            int yEnd = Math.Min(y0 + 4, h);
            for (int x = 0; x < w; x++)
            {
                for (int y = y0; y < yEnd; y++)
                {
                    int fi = (y + 1) * s + x + 1;
                    int f = flags[fi];
                    if ((f & Sig) != 0 || (f & NeighborMask) == 0)
                    {
                        continue;
                    }

                    flags[fi] |= Visited;
                    int bit = raw ? RawBit() : MqBit(sigCtx[f & NeighborMask]);
                    if (bit != 0)
                    {
                        int sign = raw ? RawBit() : DecodeSign(f);
                        SetSignificant(fi, sign, y);
                        mag[y * w + x] = 3 << bp;
                    }
                }
            }
        }
    }

    private void MagRef(int bp, bool raw)
    {
        ushort[] flags = _flags;
        int[] mag = _mag;
        int w = _w;
        int h = _h;
        int s = _stride;
        int half = 1 << bp;
        int upper = half << 1;
        for (int y0 = 0; y0 < h; y0 += 4)
        {
            int yEnd = Math.Min(y0 + 4, h);
            for (int x = 0; x < w; x++)
            {
                for (int y = y0; y < yEnd; y++)
                {
                    int fi = (y + 1) * s + x + 1;
                    int f = flags[fi];
                    if ((f & (Sig | Visited)) != Sig)
                    {
                        continue;
                    }

                    int ctx = (f & Refined) != 0 ? 16 : (f & NeighborMask) != 0 ? 15 : 14;
                    int bit = raw ? RawBit() : MqBit(ctx);
                    int mi = y * w + x;
                    // Replace the previous half-bit (at position bp+1 in half units) by the decoded bit and a new half-bit at bp.
                    mag[mi] = (mag[mi] & ~upper) | (bit << (bp + 1)) | half;
                    flags[fi] |= Refined;
                }
            }
        }
    }

    private void Cleanup(int bp, byte[] sigCtx)
    {
        ushort[] flags = _flags;
        int[] mag = _mag;
        int w = _w;
        int h = _h;
        int s = _stride;
        int value = 3 << bp;
        for (int y0 = 0; y0 < h; y0 += 4)
        {
            int yEnd = Math.Min(y0 + 4, h);
            for (int x = 0; x < w; x++)
            {
                int y = y0;
                int fi0 = (y0 + 1) * s + x + 1;
                if (y0 + 4 <= h
                    && ((flags[fi0] | flags[fi0 + s] | flags[fi0 + 2 * s] | flags[fi0 + 3 * s]) & (Sig | Visited | NeighborMask)) == 0)
                {
                    // Run-length mode (Table D.5).
                    if (MqBit(CtxRun) == 0)
                    {
                        continue;
                    }

                    int r = (MqBit(CtxUniform) << 1) | MqBit(CtxUniform);
                    y = y0 + r;
                    int fi = fi0 + (r * s);
                    int sign = DecodeSign(flags[fi]);
                    SetSignificant(fi, sign, y);
                    mag[y * w + x] = value;
                    y++;
                }

                for (; y < yEnd; y++)
                {
                    int fi = (y + 1) * s + x + 1;
                    int f = flags[fi];
                    if ((f & (Sig | Visited)) == 0)
                    {
                        if (MqBit(sigCtx[f & NeighborMask]) != 0)
                        {
                            int sign = DecodeSign(f);
                            SetSignificant(fi, sign, y);
                            mag[y * w + x] = value;
                        }
                    }
                    else
                    {
                        flags[fi] &= unchecked((ushort)~Visited);
                    }
                }
            }
        }
    }

    // ---- Context tables ----

    private static byte[] BuildSigContexts(int kind)
    {
        var table = new byte[256];
        for (int f = 0; f < 256; f++)
        {
            int v = ((f & SigN) != 0 ? 1 : 0) + ((f & SigS) != 0 ? 1 : 0);
            int hz = ((f & SigW) != 0 ? 1 : 0) + ((f & SigE) != 0 ? 1 : 0);
            int d = ((f & SigNW) != 0 ? 1 : 0) + ((f & SigNE) != 0 ? 1 : 0) + ((f & SigSW) != 0 ? 1 : 0) + ((f & SigSE) != 0 ? 1 : 0);
            int ctx;
            if (kind == 2)
            {
                int hv = hz + v;
                ctx = d >= 3 ? 8
                    : d == 2 ? (hv >= 1 ? 7 : 6)
                    : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3)
                    : (hv >= 2 ? 2 : hv == 1 ? 1 : 0);
            }
            else
            {
                int a = kind == 0 ? hz : v;
                int b = kind == 0 ? v : hz;
                ctx = a == 2 ? 8
                    : a == 1 ? (b >= 1 ? 7 : d >= 1 ? 6 : 5)
                    : b == 2 ? 4
                    : b == 1 ? 3
                    : d >= 2 ? 2
                    : d == 1 ? 1
                    : 0;
            }

            table[f] = (byte)ctx;
        }

        return table;
    }

    private static byte[] BuildSignContexts()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            // bits 0..3: N,S,W,E significant; bits 4..7: their signs (1 = negative).
            int Contribution(int bitA, int bitB)
            {
                int sum = 0;
                if ((i & bitA) != 0)
                {
                    sum += (i & (bitA << 4)) != 0 ? -1 : 1;
                }

                if ((i & bitB) != 0)
                {
                    sum += (i & (bitB << 4)) != 0 ? -1 : 1;
                }

                return Math.Clamp(sum, -1, 1);
            }

            int vc = Contribution(1, 2);
            int hc = Contribution(4, 8);
            int ctx;
            int xor = 0;
            if (hc == 1)
            {
                ctx = vc == 1 ? 13 : vc == 0 ? 12 : 11;
            }
            else if (hc == 0)
            {
                ctx = vc == 0 ? 9 : 10;
                xor = vc == -1 ? 1 : 0;
            }
            else
            {
                ctx = vc == 1 ? 11 : vc == 0 ? 12 : 13;
                xor = 1;
            }

            table[i] = (byte)(ctx | (xor << 7));
        }

        return table;
    }
}
