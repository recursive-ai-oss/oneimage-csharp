using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Compression;

/// <summary>Result of an <see cref="Inflater.Inflate"/> call.</summary>
internal enum InflateStatus
{
    /// <summary>The stream is complete and every output byte has been delivered.</summary>
    Done,

    /// <summary>All supplied input was consumed and more is needed to make progress.</summary>
    NeedInput,

    /// <summary>The output buffer is full; call again with more room (and the unconsumed input).</summary>
    NeedOutput,
}

/// <summary>
/// Incremental DEFLATE (RFC 1951) and zlib (RFC 1950) decoder. Input and output can be supplied in
/// arbitrary chunk sizes. Malformed data throws <see cref="ImageFormatException"/>; a stream that
/// would exceed the configured output cap or expansion ratio throws <see cref="ImageLimitException"/>
/// before the excess output is delivered.
/// </summary>
/// <remarks>
/// Decoded bytes go through an internal 32 KiB-history window; no allocation happens per call.
/// Call <see cref="Dispose"/> to return the pooled window (forgetting only wastes a pool buffer).
/// After <see cref="InflateStatus.Done"/> the unconsumed tail of the last input (for example PNG data
/// following the zlib stream) is exactly the bytes not reported as consumed.
/// </remarks>
internal sealed class Inflater : IDisposable
{
    private const int History = 32768;
    private const int BufferSize = History * 3;
    private const int SoftLimit = BufferSize - 258;

    // Output beyond this many bytes per input byte (plus a grace allowance for the first bytes) is rejected.
    private const long RatioGrace = 1 << 16;

    private enum State
    {
        ZlibHeader,
        BlockHeader,
        StoredHeader,
        Stored,
        DynHeader,
        DynClen,
        DynLens,
        Huffman,
        BlockEnd,
        ZlibTrailer,
        Done,
        Failed,
    }

    private enum Step
    {
        Continue,
        NeedInput,
    }

    private byte[]? _win;
    private readonly uint[] _dynLit = new uint[HuffmanTable.LitTableSize];
    private readonly uint[] _dynDist = new uint[HuffmanTable.DistTableSize];
    private readonly uint[] _clenTable = new uint[HuffmanTable.ClenTableSize];
    private readonly byte[] _lens = new byte[320];
    private readonly byte[] _clens = new byte[19];

    private uint[] _lit = HuffmanTable.FixedLit;
    private uint[] _dist = HuffmanTable.FixedDist;

    private bool _zlib;
    private long _maxOutput;
    private double _maxRatio;
    private State _state;
    private bool _final;

    private ulong _bitbuf;
    private int _bitcount;

    private int _wpos;
    private int _flushed;
    private long _outBase;
    private long _inTotal;
    private uint _adler = Adler32.Initial;

    private int _storedRemaining;
    private int _hlit;
    private int _hdist;
    private int _hclen;
    private int _idx;

    /// <summary>Creates an inflater.</summary>
    /// <param name="zlib">True for a zlib-wrapped stream (header and Adler-32 trailer), false for raw DEFLATE.</param>
    /// <param name="maxOutputBytes">Maximum total decoded bytes; <see cref="long.MaxValue"/> for none.</param>
    /// <param name="maxExpansionRatio">
    /// Maximum decoded bytes per compressed byte (with a 64 KiB allowance so tiny streams are not
    /// rejected); 0 for none. The theoretical DEFLATE maximum is about 1032.
    /// </param>
    public Inflater(bool zlib, long maxOutputBytes = long.MaxValue, double maxExpansionRatio = 0)
    {
        _win = ArrayPool<byte>.Shared.Rent(BufferSize + 272);
        Reset(zlib, maxOutputBytes, maxExpansionRatio);
    }

    /// <summary>Total decoded bytes produced so far (including bytes not yet delivered).</summary>
    public long TotalOut => _outBase + _wpos;

    /// <summary>Restarts the inflater for a new stream, reusing its buffers.</summary>
    public void Reset(bool zlib, long maxOutputBytes = long.MaxValue, double maxExpansionRatio = 0)
    {
        ObjectDisposedException.ThrowIf(_win is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxExpansionRatio);
        _zlib = zlib;
        _maxOutput = maxOutputBytes;
        _maxRatio = double.IsFinite(maxExpansionRatio) ? maxExpansionRatio : 0;
        _state = zlib ? State.ZlibHeader : State.BlockHeader;
        _final = false;
        _bitbuf = 0;
        _bitcount = 0;
        _wpos = 0;
        _flushed = 0;
        _outBase = 0;
        _inTotal = 0;
        _adler = Adler32.Initial;
        _lit = HuffmanTable.FixedLit;
        _dist = HuffmanTable.FixedDist;
    }

    /// <summary>Returns the pooled window.</summary>
    public void Dispose()
    {
        byte[]? w = _win;
        _win = null;
        if (w is not null)
        {
            ArrayPool<byte>.Shared.Return(w);
        }
    }

    /// <summary>
    /// Decodes as much as possible. Reads from <paramref name="input"/> and writes into
    /// <paramref name="output"/>; <paramref name="consumed"/> and <paramref name="written"/> report how much
    /// of each was used. When the status is <see cref="InflateStatus.NeedOutput"/> or
    /// <see cref="InflateStatus.Done"/>, input not reported as consumed must be supplied again (or belongs
    /// to whatever follows the stream). With <see cref="InflateStatus.NeedInput"/> all input was consumed.
    /// </summary>
    /// <exception cref="ImageFormatException">The data is not a valid stream.</exception>
    /// <exception cref="ImageLimitException">A configured output or ratio limit was exceeded.</exception>
    public InflateStatus Inflate(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written)
    {
        byte[] win = _win ?? throw new ObjectDisposedException(nameof(Inflater));
        if (_state == State.Failed)
        {
            throw new ImageFormatException("The stream already failed.");
        }

        int ip = 0;
        int op = 0;
        consumed = 0;
        written = 0;
        try
        {
            bool starved = false;
            while (true)
            {
                // Deliver decoded bytes first so the window can be reused.
                int pending = _wpos - _flushed;
                if (pending > 0)
                {
                    int n = Math.Min(pending, output.Length - op);
                    if (n > 0)
                    {
                        ReadOnlySpan<byte> src = win.AsSpan(_flushed, n);
                        src.CopyTo(output.Slice(op));
                        if (_zlib)
                        {
                            _adler = Adler32.Update(_adler, src);
                        }

                        _flushed += n;
                        op += n;
                    }

                    if (_flushed < _wpos)
                    {
                        consumed = ip;
                        written = op;
                        return InflateStatus.NeedOutput;
                    }
                }

                if (_state == State.Done)
                {
                    consumed = ip;
                    written = op;
                    return InflateStatus.Done;
                }

                if (starved)
                {
                    consumed = ip;
                    written = op;
                    return InflateStatus.NeedInput;
                }

                if (_wpos > SoftLimit)
                {
                    // Everything is delivered: keep the last 32 KiB as history and start over at its end.
                    _outBase += _wpos - History;
                    win.AsSpan(_wpos - History, History).CopyTo(win);
                    _wpos = History;
                    _flushed = History;
                }

                int before = ip;
                Step step = Run(input, ref ip, win);
                _inTotal += ip - before;
                CheckLimits();
                starved = step == Step.NeedInput;
                if (_state == State.Done)
                {
                    PushBack(ref ip);
                }
            }
        }
        catch (ImageException)
        {
            _state = State.Failed;
            throw;
        }
    }

    private void PushBack(ref int ip)
    {
        // Whole bytes left in the bit buffer after the last needed bit belong to whatever follows the stream.
        // The newest buffered bytes always come from the call that finished the stream.
        int back = Math.Min(_bitcount >> 3, ip);
        ip -= back;
        _inTotal -= back;
        _bitbuf = 0;
        _bitcount = 0;
    }

    private void CheckLimits()
    {
        long produced = _outBase + _wpos;
        if (produced > _maxOutput)
        {
            throw new ImageLimitException("Decompressed data exceeds the configured output limit.");
        }

        if (_maxRatio > 0 && produced > ((long)(_maxRatio * _inTotal)) + RatioGrace)
        {
            throw new ImageLimitException("Decompressed data exceeds the configured expansion ratio.");
        }
    }

    private Step Run(ReadOnlySpan<byte> input, ref int ip, byte[] win)
    {
        while (true)
        {
            switch (_state)
            {
                case State.ZlibHeader:
                {
                    Refill(input, ref ip);
                    if (_bitcount < 16)
                    {
                        return Step.NeedInput;
                    }

                    int cmf = (int)(_bitbuf & 0xFF);
                    int flg = (int)((_bitbuf >> 8) & 0xFF);
                    Consume(16);
                    if ((cmf & 0x0F) != 8)
                    {
                        throw new ImageFormatException("Unsupported zlib compression method.");
                    }

                    if ((cmf >> 4) > 7)
                    {
                        throw new ImageFormatException("Invalid zlib window size.");
                    }

                    if ((((cmf << 8) | flg) % 31) != 0)
                    {
                        throw new ImageFormatException("Bad zlib header check.");
                    }

                    if ((flg & 0x20) != 0)
                    {
                        throw new ImageFormatException("zlib preset dictionaries are not supported.");
                    }

                    _state = State.BlockHeader;
                    break;
                }

                case State.BlockHeader:
                {
                    Refill(input, ref ip);
                    if (_bitcount < 3)
                    {
                        return Step.NeedInput;
                    }

                    _final = (_bitbuf & 1) != 0;
                    int type = (int)((_bitbuf >> 1) & 3);
                    Consume(3);
                    switch (type)
                    {
                        case 0:
                            Consume(_bitcount & 7);
                            _state = State.StoredHeader;
                            break;
                        case 1:
                            _lit = HuffmanTable.FixedLit;
                            _dist = HuffmanTable.FixedDist;
                            _state = State.Huffman;
                            break;
                        case 2:
                            _state = State.DynHeader;
                            break;
                        default:
                            throw new ImageFormatException("Invalid DEFLATE block type.");
                    }

                    break;
                }

                case State.StoredHeader:
                {
                    Refill(input, ref ip);
                    if (_bitcount < 32)
                    {
                        return Step.NeedInput;
                    }

                    int len = (int)(_bitbuf & 0xFFFF);
                    int nlen = (int)((_bitbuf >> 16) & 0xFFFF);
                    Consume(32);
                    if ((len ^ 0xFFFF) != nlen)
                    {
                        throw new ImageFormatException("Stored block length check failed.");
                    }

                    _storedRemaining = len;
                    _state = State.Stored;
                    break;
                }

                case State.Stored:
                {
                    int room = BufferSize - _wpos;
                    while (_storedRemaining > 0 && room > 0 && _bitcount >= 8)
                    {
                        win[_wpos++] = (byte)_bitbuf;
                        Consume(8);
                        _storedRemaining--;
                        room--;
                    }

                    if (_storedRemaining > 0 && room > 0)
                    {
                        int n = Math.Min(Math.Min(_storedRemaining, room), input.Length - ip);
                        input.Slice(ip, n).CopyTo(win.AsSpan(_wpos));
                        ip += n;
                        _wpos += n;
                        _storedRemaining -= n;
                        if (_storedRemaining > 0 && ip >= input.Length)
                        {
                            return Step.NeedInput;
                        }
                    }

                    if (_storedRemaining == 0)
                    {
                        _state = State.BlockEnd;
                    }
                    else
                    {
                        return Step.Continue;
                    }

                    break;
                }

                case State.DynHeader:
                {
                    Refill(input, ref ip);
                    if (_bitcount < 14)
                    {
                        return Step.NeedInput;
                    }

                    _hlit = (int)(_bitbuf & 31) + 257;
                    _hdist = (int)((_bitbuf >> 5) & 31) + 1;
                    _hclen = (int)((_bitbuf >> 10) & 15) + 4;
                    Consume(14);
                    if (_hlit > 286 || _hdist > 30)
                    {
                        throw new ImageFormatException("Too many length or distance codes.");
                    }

                    Array.Clear(_clens);
                    _idx = 0;
                    _state = State.DynClen;
                    break;
                }

                case State.DynClen:
                {
                    while (_idx < _hclen)
                    {
                        Refill(input, ref ip);
                        if (_bitcount < 3)
                        {
                            return Step.NeedInput;
                        }

                        _clens[DeflateTables.ClenOrder[_idx++]] = (byte)(_bitbuf & 7);
                        Consume(3);
                    }

                    string? err = HuffmanTable.Build(_clens, HuffmanTable.ClenSymbols, HuffmanTable.ClenRootBits, _clenTable, false);
                    if (err is not null)
                    {
                        throw new ImageFormatException("Invalid code length code: " + err + ".");
                    }

                    _idx = 0;
                    _state = State.DynLens;
                    break;
                }

                case State.DynLens:
                {
                    int total = _hlit + _hdist;
                    while (_idx < total)
                    {
                        Refill(input, ref ip);
                        uint e = _clenTable[(int)(_bitbuf & ((1 << HuffmanTable.ClenRootBits) - 1))];
                        int clen = (int)(e & 0xFF);
                        if (clen > _bitcount)
                        {
                            return Step.NeedInput;
                        }

                        int sym = (int)(e >> 16);
                        if (sym < 16)
                        {
                            _lens[_idx++] = (byte)sym;
                            Consume(clen);
                            continue;
                        }

                        int extraBits = sym == 16 ? 2 : sym == 17 ? 3 : 7;
                        if (clen + extraBits > _bitcount)
                        {
                            return Step.NeedInput;
                        }

                        int x = (int)((_bitbuf >> clen) & ((1u << extraBits) - 1));
                        int rep;
                        byte value;
                        if (sym == 16)
                        {
                            if (_idx == 0)
                            {
                                throw new ImageFormatException("Repeat code with no previous length.");
                            }

                            value = _lens[_idx - 1];
                            rep = 3 + x;
                        }
                        else
                        {
                            value = 0;
                            rep = (sym == 17 ? 3 : 11) + x;
                        }

                        if (_idx + rep > total)
                        {
                            throw new ImageFormatException("Code length repeat overflows the table.");
                        }

                        Consume(clen + extraBits);
                        Array.Fill(_lens, value, _idx, rep);
                        _idx += rep;
                    }

                    if (_lens[256] == 0)
                    {
                        throw new ImageFormatException("Missing end-of-block code.");
                    }

                    string? e1 = HuffmanTable.Build(_lens.AsSpan(0, _hlit), HuffmanTable.LitSymbols, HuffmanTable.LitRootBits, _dynLit, true);
                    if (e1 is not null)
                    {
                        throw new ImageFormatException("Invalid literal/length code: " + e1 + ".");
                    }

                    string? e2 = HuffmanTable.Build(_lens.AsSpan(_hlit, _hdist), HuffmanTable.DistSymbols, HuffmanTable.DistRootBits, _dynDist, true);
                    if (e2 is not null)
                    {
                        throw new ImageFormatException("Invalid distance code: " + e2 + ".");
                    }

                    _lit = _dynLit;
                    _dist = _dynDist;
                    _state = State.Huffman;
                    break;
                }

                case State.Huffman:
                {
                    int r = DecodeBlock(input, ref ip, win);
                    if (r == 0)
                    {
                        _state = State.BlockEnd;
                    }
                    else if (r == 1)
                    {
                        return Step.NeedInput;
                    }
                    else
                    {
                        return Step.Continue;
                    }

                    break;
                }

                case State.BlockEnd:
                    if (!_final)
                    {
                        _state = State.BlockHeader;
                    }
                    else
                    {
                        Consume(_bitcount & 7);
                        _state = _zlib ? State.ZlibTrailer : State.Done;
                    }

                    break;

                case State.ZlibTrailer:
                {
                    // Every byte must be delivered before the checksum is final.
                    if (_flushed < _wpos)
                    {
                        return Step.Continue;
                    }

                    Refill(input, ref ip);
                    if (_bitcount < 32)
                    {
                        return Step.NeedInput;
                    }

                    uint expected = BinaryPrimitives.ReverseEndianness((uint)_bitbuf);
                    Consume(32);
                    if (expected != _adler)
                    {
                        throw new ImageFormatException("zlib Adler-32 checksum mismatch.");
                    }

                    _state = State.Done;
                    break;
                }

                default:
                    return Step.Continue;
            }

            if (_state == State.Done)
            {
                return Step.Continue;
            }
        }
    }

    private void Consume(int n)
    {
        _bitbuf >>= n;
        _bitcount -= n;
    }

    private void Refill(ReadOnlySpan<byte> input, ref int ip)
    {
        while (_bitcount < 56 && ip < input.Length)
        {
            _bitbuf |= (ulong)input[ip++] << _bitcount;
            _bitcount += 8;
        }
    }

    /// <summary>Hot loop. Returns 0 at end of block, 1 when more input is needed, 2 when the window is full.</summary>
    private int DecodeBlock(ReadOnlySpan<byte> input, ref int inPos, byte[] win)
    {
        ulong bitbuf = _bitbuf;
        int bitcount = _bitcount;
        int ip = inPos;
        int wp = _wpos;
        uint[] lit = _lit;
        uint[] dst = _dist;
        const int LitMask = (1 << HuffmanTable.LitRootBits) - 1;
        const int DistMask = (1 << HuffmanTable.DistRootBits) - 1;
        int result;
        while (true)
        {
            if (wp > SoftLimit)
            {
                result = 2;
                break;
            }

            if (ip <= input.Length - 8)
            {
                // Branch-free refill: bits above 'bitcount' repeat real upcoming input, which is harmless.
                bitbuf |= BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(ip)) << bitcount;
                ip += (63 - bitcount) >> 3;
                bitcount |= 56;
            }
            else
            {
                while (bitcount < 56 && ip < input.Length)
                {
                    bitbuf |= (ulong)input[ip++] << bitcount;
                    bitcount += 8;
                }
            }

            uint e = lit[(int)(bitbuf & LitMask)];
            if ((e & HuffmanTable.TypeMask) == HuffmanTable.TypeSub)
            {
                e = lit[(int)(e >> 16) + (int)((bitbuf >> HuffmanTable.LitRootBits) & ((1u << (int)((e >> 12) & 15)) - 1))];
            }

            int clen = (int)(e & 0xFF);
            if (clen > bitcount)
            {
                result = 1;
                break;
            }

            uint type = e & HuffmanTable.TypeMask;
            if (type == HuffmanTable.TypeLiteral)
            {
                bitbuf >>= clen;
                bitcount -= clen;
                win[wp++] = (byte)(e >> 16);
                continue;
            }

            if (type == HuffmanTable.TypeBase)
            {
                int extra = (int)((e >> 12) & 15);
                if (clen + extra > bitcount)
                {
                    result = 1;
                    break;
                }

                int length = (int)(e >> 16) + (int)((bitbuf >> clen) & ((1UL << extra) - 1));
                ulong tb = bitbuf >> (clen + extra);
                int tc = bitcount - clen - extra;

                uint d = dst[(int)(tb & DistMask)];
                if ((d & HuffmanTable.TypeMask) == HuffmanTable.TypeSub)
                {
                    d = dst[(int)(d >> 16) + (int)((tb >> HuffmanTable.DistRootBits) & ((1u << (int)((d >> 12) & 15)) - 1))];
                }

                int dl = (int)(d & 0xFF);
                if (dl > tc)
                {
                    result = 1;
                    break;
                }

                if ((d & HuffmanTable.TypeMask) != HuffmanTable.TypeBase)
                {
                    throw new ImageFormatException("Invalid distance code.");
                }

                int dextra = (int)((d >> 12) & 15);
                if (dl + dextra > tc)
                {
                    result = 1;
                    break;
                }

                int dist = (int)(d >> 16) + (int)((tb >> dl) & ((1UL << dextra) - 1));
                if (dist > wp)
                {
                    throw new ImageFormatException("Distance reaches before the start of the data.");
                }

                bitbuf = tb >> (dl + dextra);
                bitcount = tc - dl - dextra;

                // Copy the match. The window has slack after the soft limit, so a few bytes of
                // overshoot in the 8-byte copy are harmless.
                int src = wp - dist;
                if (dist >= 8)
                {
                    Span<byte> w = win;
                    int s = src;
                    int t = wp;
                    int n = length;
                    do
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(w.Slice(t), BinaryPrimitives.ReadUInt64LittleEndian(w.Slice(s)));
                        s += 8;
                        t += 8;
                        n -= 8;
                    }
                    while (n > 0);
                }
                else if (dist == 1)
                {
                    win.AsSpan(wp, length).Fill(win[src]);
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        win[wp + i] = win[src + i];
                    }
                }

                wp += length;
                continue;
            }

            if (type == HuffmanTable.TypeEndOfBlock)
            {
                bitbuf >>= clen;
                bitcount -= clen;
                result = 0;
                break;
            }

            throw new ImageFormatException("Invalid literal/length code.");
        }

        _bitbuf = bitcount >= 64 ? bitbuf : bitbuf & ((1UL << bitcount) - 1);
        _bitcount = bitcount;
        inPos = ip;
        _wpos = wp;
        return result;
    }
}
