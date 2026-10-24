using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Compression;

/// <summary>
/// DEFLATE (RFC 1951) / zlib (RFC 1950) compressor with streaming input and zlib-style levels 0-9
/// (0 stores). Output goes to an <see cref="IBufferWriter{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The match finder is a hash-chain with lazy matching (levels 4-9) or greedy matching (1-3), tuned
/// like zlib's configuration table. Blocks choose between stored, fixed and dynamic Huffman by exact
/// cost. Everything is deterministic: the same input at the same level yields the same bytes, and the
/// result does not depend on how the input is split across <see cref="Write"/> calls.
/// </para>
/// <para>
/// Call <see cref="Finish"/> once to end the stream, then <see cref="Dispose"/> to return pooled
/// buffers. <see cref="Reset(int, bool)"/> starts a new stream on the same instance.
/// </para>
/// </remarks>
internal sealed class Deflater : IDisposable
{
    private const int WSize = 32768;
    private const int WMask = WSize - 1;
    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int MinLookahead = MaxMatch + MinMatch + 1;
    private const int MaxDist = WSize - MinLookahead;
    private const int HashBits = 15;
    private const int WindowAlloc = (2 * WSize) + 320;
    private const int SymBufSize = 16384;
    private const int SymEnd = SymBufSize - 1;
    private const int OutSize = 32768;
    private const int MaxStored = 65535;
    private const int LazySkipShift = 6;

    // good_length, max_lazy (max_insert for levels 1-3), nice_length, max_chain
    private static readonly (int Good, int Lazy, int Nice, int Chain)[] Config =
    {
        (0, 0, 0, 0),
        (4, 4, 8, 4),
        (4, 5, 16, 8),
        (4, 6, 32, 32),
        (4, 4, 16, 16),
        (8, 16, 32, 32),
        (8, 16, 128, 128),
        (8, 32, 128, 256),
        (32, 128, 258, 1024),
        (32, 258, 258, 4096),
    };

    private static readonly uint[] FixedLitEnc = BuildFixedLit();
    private static readonly uint[] FixedDistEnc = BuildFixedDist();

    private IBufferWriter<byte> _sink;
    private int _level;
    private bool _zlib;
    private bool _finished;
    private bool _disposed;
    private bool _filtered;
    private int _miss;
    private int _hashLen = 4;
    private ulong _hashMask = 0xFFFFFFFFUL;
    private int _minUseful = MinMatch;

    private byte[] _window = Array.Empty<byte>();
    private ushort[] _head = Array.Empty<ushort>();
    private ushort[] _prev = Array.Empty<ushort>();
    private byte[] _out = Array.Empty<byte>();

    private int _strstart;
    private int _lookahead;
    private int _blockStart;
    private int _matchStart;
    private int _matchLength;
    private int _prevLength;
    private int _prevMatch;
    private bool _matchAvailable;
    private int _storedLen;

    private int _goodMatch;
    private int _maxLazy;
    private int _niceMatch;
    private int _maxChain;

    private readonly byte[] _symLit = new byte[SymBufSize];
    private readonly ushort[] _symDist = new ushort[SymBufSize];
    private int _symNext;
    private readonly int[] _litFreq = new int[288];
    private readonly int[] _distFreq = new int[32];

    private ulong _bitBuf;
    private int _bitCnt;
    private int _outPos;
    private uint _adler;

    // Scratch for block building.
    private readonly byte[] _litLens = new byte[288];
    private readonly byte[] _distLens = new byte[32];
    private readonly byte[] _clenLens = new byte[19];
    private readonly uint[] _litEnc = new uint[288];
    private readonly uint[] _distEnc = new uint[32];
    private readonly uint[] _clenEnc = new uint[19];
    private readonly int[] _clenFreq = new int[19];
    private readonly byte[] _allLens = new byte[320];
    private readonly byte[] _tokSym = new byte[330];
    private readonly byte[] _tokExtra = new byte[330];
    private readonly ulong[] _keys = new ulong[288];
    private readonly int[] _nodeW = new int[576];
    private readonly int[] _nodeP = new int[576];
    private readonly int[] _depth = new int[576];

    /// <summary>Creates a compressor.</summary>
    /// <param name="sink">Receives the compressed bytes.</param>
    /// <param name="level">0 (stored) to 9 (smallest); 6 is the usual default.</param>
    /// <param name="zlib">True to wrap the stream in a zlib header and Adler-32 trailer.</param>
    public Deflater(IBufferWriter<byte> sink, int level = 6, bool zlib = true)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _out = Pools<byte>.Pool.Rent(OutSize);
        _window = Pools<byte>.Pool.Rent(WindowAlloc);
        Reset(level, zlib);
    }

    /// <summary>
    /// Gets or sets the "filtered" strategy: matches of 5 bytes or fewer are not used and become literals (like zlib's
    /// Z_FILTERED, but it applies to levels 1-9). It suits data that went through predictive filtering, such as PNG
    /// scanlines, where short matches are mostly coincidences. Because only longer matches count, the match finder hashes
    /// 6 bytes instead of 3, which keeps the candidate chains short and makes compression much faster. Set it before the
    /// first <see cref="Write"/>; it is kept across <see cref="Reset(int, bool)"/>.
    /// </summary>
    public bool FilteredStrategy
    {
        get => _filtered;
        set
        {
            _filtered = value;
            ConfigureHash();
        }
    }

    // Shorter matches than this are not worth emitting. The hash covers the same number of bytes (4 or 6) so chains
    // hold almost only candidates that can reach it: with filtered data a 6-byte hash keeps the chains short.
    private void ConfigureHash()
    {
        _hashLen = _filtered ? 6 : 3;
        _hashMask = _filtered ? 0x0000FFFFFFFFFFFFUL : 0x0000000000FFFFFFUL;
        _minUseful = _filtered ? 6 : MinMatch;
        if (_level > 0)
        {
            ApplyLevelConfig();
        }
    }

    // zlib's table, except that level 6 on unfiltered data searches a little less: it gives up about 0.4% in size for
    // roughly a fifth of the time. Filtered (PNG-style) data keeps the full search, where long matches pay off.
    private void ApplyLevelConfig()
    {
        (_goodMatch, _maxLazy, _niceMatch, _maxChain) = Config[_level];
        if (_level == 6 && !_filtered)
        {
            (_goodMatch, _maxLazy, _niceMatch, _maxChain) = (8, 12, 64, 64);
        }
    }

    /// <summary>Starts a new stream writing to <paramref name="sink"/>, keeping the buffers.</summary>
    public void Reset(IBufferWriter<byte> sink, int level, bool zlib)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        Reset(level, zlib);
    }

    /// <summary>Starts a new stream with the given settings, keeping the buffers.</summary>
    public void Reset(int level, bool zlib)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _level = level;
        _zlib = zlib;
        _finished = false;
        if (level > 0)
        {
            if (_head.Length == 0)
            {
                _head = Pools<ushort>.Pool.Rent(1 << HashBits);
                _prev = Pools<ushort>.Pool.Rent(WSize);
            }

            Array.Clear(_head, 0, 1 << HashBits);
            ApplyLevelConfig();
        }

        _strstart = 0;
        _lookahead = 0;
        _blockStart = 0;
        _matchStart = 0;
        _matchLength = MinMatch - 1;
        _prevLength = MinMatch - 1;
        _prevMatch = 0;
        _matchAvailable = false;
        _miss = 0;
        _storedLen = 0;
        _symNext = 0;
        Array.Clear(_litFreq);
        Array.Clear(_distFreq);
        _bitBuf = 0;
        _bitCnt = 0;
        _outPos = 0;
        _adler = Adler32.Initial;

        if (zlib)
        {
            int flevel = level < 2 ? 0 : level < 6 ? 1 : level == 6 ? 2 : 3;
            int hdr = (0x78 << 8) | (flevel << 6);
            hdr += 31 - (hdr % 31);
            _out[_outPos++] = (byte)(hdr >> 8);
            _out[_outPos++] = (byte)hdr;
        }
    }

    /// <summary>Returns pooled buffers. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Pools<byte>.Pool.Return(_window);
        Pools<byte>.Pool.Return(_out);
        if (_head.Length != 0)
        {
            Pools<ushort>.Pool.Return(_head);
            Pools<ushort>.Pool.Return(_prev);
        }

        _window = Array.Empty<byte>();
        _out = Array.Empty<byte>();
        _head = Array.Empty<ushort>();
        _prev = Array.Empty<ushort>();
    }

    /// <summary>Compresses a whole buffer in one call and returns the bytes.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int level = 6, bool zlib = true)
    {
        var sink = new ArrayBufferWriter<byte>(Math.Max(64, data.Length / 2));
        using var d = new Deflater(sink, level, zlib);
        d.Write(data);
        d.Finish();
        return sink.WrittenSpan.ToArray();
    }

    /// <summary>Feeds more input. May write compressed bytes to the sink.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The stream is already finished.");
        }

        if (_zlib)
        {
            _adler = Adler32.Update(_adler, data);
        }

        if (_level == 0)
        {
            WriteStored(data);
            return;
        }

        while (data.Length > 0)
        {
            int n = FillWindow(data);
            data = data.Slice(n);
            Process(false);
        }
    }

    /// <summary>Ends the stream: flushes the last block and the trailer.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        if (_level == 0)
        {
            EmitStored(_window.AsSpan(0, _storedLen), true);
            _storedLen = 0;
        }
        else
        {
            Process(true);
            if (_matchAvailable)
            {
                TallyLit(_window[_strstart - 1]);
                _matchAvailable = false;
            }

            FlushBlock(true);
        }

        if (_zlib)
        {
            FlushBitsToByte();
            EnsureOut(4);
            BinaryPrimitives.WriteUInt32BigEndian(_out.AsSpan(_outPos), _adler);
            _outPos += 4;
        }

        FlushBitsToByte();
        Drain();
        _finished = true;
    }

    // ---- level 0 -------------------------------------------------------------------------------

    private void WriteStored(ReadOnlySpan<byte> data)
    {
        byte[] buf = _window;
        while (data.Length > 0)
        {
            if (_storedLen == 0 && data.Length > MaxStored)
            {
                // Large input: emit directly without copying.
                EmitStored(data.Slice(0, MaxStored), false);
                data = data.Slice(MaxStored);
                continue;
            }

            int n = Math.Min(data.Length, MaxStored - _storedLen);
            data.Slice(0, n).CopyTo(buf.AsSpan(_storedLen));
            _storedLen += n;
            data = data.Slice(n);
            if (_storedLen == MaxStored)
            {
                EmitStored(buf.AsSpan(0, MaxStored), false);
                _storedLen = 0;
            }
        }
    }

    private void EmitStored(ReadOnlySpan<byte> data, bool last)
    {
        EnsureOut(16);
        PutBits(last ? 1u : 0u, 3);
        FlushBitsToByte();
        BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(_outPos), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(_outPos + 2), (ushort)~data.Length);
        _outPos += 4;
        Drain();
        if (data.Length > 0)
        {
            _sink.Write(data);
        }
    }

    // ---- window and matching -------------------------------------------------------------------

    private int FillWindow(ReadOnlySpan<byte> data)
    {
        if (_strstart >= WSize + MaxDist)
        {
            Slide();
        }

        int space = (2 * WSize) - (_strstart + _lookahead);
        int n = Math.Min(space, data.Length);
        data.Slice(0, n).CopyTo(_window.AsSpan(_strstart + _lookahead));
        _lookahead += n;
        return n;
    }

    private void Slide()
    {
        byte[] w = _window;
        ushort[] head = _head;
        ushort[] prev = _prev;
        w.AsSpan(WSize, WSize).CopyTo(w);
        SlideTable(head.AsSpan(0, 1 << HashBits));
        SlideTable(prev.AsSpan(0, WSize));

        _strstart -= WSize;
        _matchStart -= WSize;
        _prevMatch -= WSize;
        _blockStart -= WSize;
    }

    // Entries become max(entry, WSize) - WSize: positions that fall out of the window turn into 0 ("none").
    private static void SlideTable(Span<ushort> table)
    {
        var k = new Vector<ushort>((ushort)WSize);
        int i = 0;
        for (; i <= table.Length - Vector<ushort>.Count; i += Vector<ushort>.Count)
        {
            Vector<ushort> v = new Vector<ushort>(table.Slice(i));
            (Vector.Max(v, k) - k).CopyTo(table.Slice(i));
        }

        for (; i < table.Length; i++)
        {
            int m = table[i];
            table[i] = (ushort)(m >= WSize ? m - WSize : 0);
        }
    }

    private int InsertString(int pos)
    {
        byte[] w = _window;
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(w.AsSpan(pos)) & _hashMask;
        int h = (int)((v * 0x9E3779B97F4A7C15UL) >> (64 - HashBits));
        ushort[] head = _head;
        int hd = head[h];
        _prev[pos & WMask] = (ushort)hd;
        head[h] = (ushort)pos;
        return hd;
    }

    private int LongestMatch(int curMatch, int prevLen)
    {
        int chain = _maxChain;
        if (prevLen >= _goodMatch)
        {
            chain >>= 2;
        }

        int maxLen = Math.Min(MaxMatch, _lookahead);
        int nice = Math.Min(_niceMatch, maxLen);

        // Matches shorter than _minUseful are discarded by the caller, so only longer ones are worth finding.
        int best = Math.Max(prevLen, _minUseful - 1);
        int scan = _strstart;
        int limit = scan > MaxDist ? scan - MaxDist : 0;
        byte[] w = _window;
        ushort[] prev = _prev;
        if (best >= maxLen)
        {
            return Math.Min(best, _lookahead);
        }

        ulong scanHead = BinaryPrimitives.ReadUInt64LittleEndian(w.AsSpan(scan));
        while (true)
        {
            if (w[curMatch + best] == w[scan + best])
            {
                int len;
                ulong x = BinaryPrimitives.ReadUInt64LittleEndian(w.AsSpan(curMatch)) ^ scanHead;
                if (x != 0)
                {
                    len = BitOperations.TrailingZeroCount(x) >> 3;
                }
                else
                {
                    len = 8;
                    while (len < maxLen)
                    {
                        x = BinaryPrimitives.ReadUInt64LittleEndian(w.AsSpan(scan + len)) ^ BinaryPrimitives.ReadUInt64LittleEndian(w.AsSpan(curMatch + len));
                        if (x != 0)
                        {
                            len += BitOperations.TrailingZeroCount(x) >> 3;
                            break;
                        }

                        len += 8;
                    }
                }

                if (len > maxLen)
                {
                    len = maxLen;
                }

                if (len > best)
                {
                    _matchStart = curMatch;
                    best = len;
                    if (len >= nice || len >= maxLen)
                    {
                        break;
                    }
                }
            }

            curMatch = prev[curMatch & WMask];
            if (curMatch <= limit || --chain == 0)
            {
                break;
            }
        }

        return Math.Min(best, _lookahead);
    }

    private void Process(bool finish)
    {
        if (_level <= 3)
        {
            ProcessFast(finish);
        }
        else
        {
            ProcessLazy(finish);
        }
    }

    private void ProcessFast(bool finish)
    {
        byte[] w = _window;
        while (true)
        {
            if (_strstart >= WSize + MaxDist)
            {
                Slide();
            }

            if (_lookahead < MinLookahead && !finish)
            {
                return;
            }

            if (_lookahead == 0)
            {
                return;
            }

            int hashHead = 0;
            if (_lookahead >= _hashLen)
            {
                hashHead = InsertString(_strstart);
            }

            _matchLength = MinMatch - 1;
            if (hashHead != 0 && _strstart - hashHead <= MaxDist)
            {
                _matchLength = LongestMatch(hashHead, MinMatch - 1);
            }

            bool flush;
            if (_matchLength >= _minUseful)
            {
                _miss = 0;
                flush = TallyDist(_strstart - _matchStart, _matchLength - MinMatch);
                _lookahead -= _matchLength;
                if (_matchLength <= _maxLazy && _lookahead >= _hashLen)
                {
                    _matchLength--;
                    do
                    {
                        _strstart++;
                        InsertString(_strstart);
                    }
                    while (--_matchLength != 0);
                    _strstart++;
                }
                else
                {
                    _strstart += _matchLength;
                    _matchLength = 0;
                }
            }
            else
            {
                // Long stretches without a match are probably incompressible: skip the search for some of the
                // following positions (they become literals) and look harder less often. Matches resume the
                // normal pace.
                int step = Math.Min(1 + (_miss >> 5), Math.Min(_lookahead, 8));
                _miss++;
                flush = false;
                for (int k = 0; k < step; k++)
                {
                    flush |= TallyLit(w[_strstart]);
                    _lookahead--;
                    _strstart++;
                    if (flush)
                    {
                        FlushBlock(false);
                        flush = false;
                    }
                }
            }

            if (flush)
            {
                FlushBlock(false);
            }
        }
    }

    private void ProcessLazy(bool finish)
    {
        byte[] w = _window;
        while (true)
        {
            if (_strstart >= WSize + MaxDist)
            {
                Slide();
            }

            if (_lookahead < MinLookahead && !finish)
            {
                return;
            }

            if (_lookahead == 0)
            {
                return;
            }

            int hashHead = 0;
            if (_lookahead >= _hashLen)
            {
                hashHead = InsertString(_strstart);
            }

            _prevLength = _matchLength;
            _prevMatch = _matchStart;
            _matchLength = MinMatch - 1;
            if (hashHead != 0 && _prevLength < _maxLazy && _strstart - hashHead <= MaxDist)
            {
                _matchLength = LongestMatch(hashHead, _prevLength);
                // A distant length-3 match costs more than the three literals it replaces.
                if ((_matchLength == 3 && _strstart - _matchStart > 4096) || _matchLength < _minUseful)
                {
                    _matchLength = MinMatch - 1;
                }
            }

            if (_prevLength >= MinMatch && _matchLength <= _prevLength)
            {
                int maxInsert = _strstart + _lookahead - _hashLen;
                _miss = 0;
                bool flush = TallyDist(_strstart - 1 - _prevMatch, _prevLength - MinMatch);
                _lookahead -= _prevLength - 1;
                _prevLength -= 2;
                do
                {
                    if (++_strstart <= maxInsert)
                    {
                        InsertString(_strstart);
                    }
                }
                while (--_prevLength != 0);
                _matchAvailable = false;
                _matchLength = MinMatch - 1;
                _strstart++;
                if (flush)
                {
                    FlushBlock(false);
                }
            }
            else if (_matchAvailable)
            {
                // After a long run without any match, skip searching a few positions (see ProcessFast).
                int steps = _matchLength >= MinMatch ? 1 : Math.Min(1 + Math.Min(_miss >> LazySkipShift, 7), _lookahead);
                _miss++;
                for (int k = 0; k < steps; k++)
                {
                    if (TallyLit(w[_strstart - 1]))
                    {
                        FlushBlock(false);
                    }

                    _strstart++;
                    _lookahead--;
                }
            }
            else
            {
                _matchAvailable = true;
                _strstart++;
                _lookahead--;
            }
        }
    }

    private bool TallyLit(byte c)
    {
        _symLit[_symNext] = c;
        _symDist[_symNext] = 0;
        _symNext++;
        _litFreq[c]++;
        return _symNext == SymEnd;
    }

    private bool TallyDist(int dist, int lenMinus3)
    {
        _symLit[_symNext] = (byte)lenMinus3;
        _symDist[_symNext] = (ushort)dist;
        _symNext++;
        _litFreq[257 + DeflateTables.LengthSymbol[lenMinus3]]++;
        _distFreq[DeflateTables.DistanceSymbol(dist)]++;
        return _symNext == SymEnd;
    }

    // ---- block output --------------------------------------------------------------------------

    private void FlushBlock(bool last)
    {
        _litFreq[256] = 1;
        int storedLen = _strstart - _blockStart;
        bool storedOk = _blockStart >= 0 && storedLen <= MaxStored;

        BuildLengths(_litFreq, _litLens, 15);
        BuildLengths(_distFreq.AsSpan(0, 30), _distLens.AsSpan(0, 30), 15);
        int hlit = 286;
        while (hlit > 257 && _litLens[hlit - 1] == 0)
        {
            hlit--;
        }

        int hdist = 30;
        while (hdist > 1 && _distLens[hdist - 1] == 0)
        {
            hdist--;
        }

        _litLens.AsSpan(0, hlit).CopyTo(_allLens);
        _distLens.AsSpan(0, hdist).CopyTo(_allLens.AsSpan(hlit));
        int tokens = TokenizeLengths(hlit + hdist);
        Array.Clear(_clenFreq);
        for (int i = 0; i < tokens; i++)
        {
            _clenFreq[_tokSym[i]]++;
        }

        BuildLengths(_clenFreq, _clenLens, 7);
        int hclen = 19;
        while (hclen > 4 && _clenLens[DeflateTables.ClenOrder[hclen - 1]] == 0)
        {
            hclen--;
        }

        long dynBits = 3 + 14 + (3 * hclen);
        for (int s = 0; s < 19; s++)
        {
            dynBits += (long)_clenFreq[s] * (_clenLens[s] + (s == 16 ? 2 : s == 17 ? 3 : s == 18 ? 7 : 0));
        }

        long fixedBits = 3;
        for (int s = 0; s < 286; s++)
        {
            int f = _litFreq[s];
            if (f == 0)
            {
                continue;
            }

            int extra = s > 256 ? DeflateTables.LengthExtra[s - 257] : 0;
            dynBits += (long)f * (_litLens[s] + extra);
            fixedBits += (long)f * (FixedLen(s) + extra);
        }

        for (int s = 0; s < 30; s++)
        {
            int f = _distFreq[s];
            dynBits += (long)f * (_distLens[s] + DeflateTables.DistExtra[s]);
            fixedBits += (long)f * (5 + DeflateTables.DistExtra[s]);
        }

        long storedBits = storedOk ? 3 + 7 + 32 + (8L * storedLen) : long.MaxValue;

        EnsureOut(16);
        if (storedBits < dynBits && storedBits < fixedBits)
        {
            // Flush any symbols' worth of state; the window still holds the raw bytes.
            EmitStored(_window.AsSpan(_blockStart, storedLen), last);
        }
        else if (fixedBits <= dynBits)
        {
            PutBits(last ? 3u : 2u, 3);
            EncodeSymbols(FixedLitEnc, FixedDistEnc);
        }
        else
        {
            PutBits(last ? 5u : 4u, 3);
            PutBits((uint)(hlit - 257), 5);
            PutBits((uint)(hdist - 1), 5);
            PutBits((uint)(hclen - 4), 4);
            MakeCodes(_clenLens, _clenEnc);
            for (int i = 0; i < hclen; i++)
            {
                PutBits(_clenLens[DeflateTables.ClenOrder[i]], 3);
            }

            for (int i = 0; i < tokens; i++)
            {
                EnsureOut(16);
                int s = _tokSym[i];
                uint e = _clenEnc[s];
                PutBits(e & 0xFFFF, (int)(e >> 16));
                if (s >= 16)
                {
                    PutBits(_tokExtra[i], s == 16 ? 2 : s == 17 ? 3 : 7);
                }
            }

            MakeCodes(_litLens, _litEnc);
            MakeCodes(_distLens.AsSpan(0, 30), _distEnc);
            EncodeSymbols(_litEnc, _distEnc);
        }

        _symNext = 0;
        Array.Clear(_litFreq);
        Array.Clear(_distFreq);
        _blockStart = _strstart;
    }

    private static int FixedLen(int s) => s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8;

    private void EncodeSymbols(uint[] litEnc, uint[] distEnc)
    {
        byte[] lits = _symLit;
        ushort[] dists = _symDist;
        byte[] lenSym = DeflateTables.LengthSymbol;
        byte[] lenExtra = DeflateTables.LengthExtra;
        ushort[] lenBase = DeflateTables.LengthBase;
        ushort[] distBase = DeflateTables.DistBase;
        byte[] distExtra = DeflateTables.DistExtra;
        byte[] o = _out;
        int count = _symNext;

        // The bit accumulator lives in locals for the whole block; it holds fewer than 32 bits between symbols.
        ulong bb = _bitBuf;
        int bc = _bitCnt;
        int op = _outPos;
        for (int i = 0; i < count; i++)
        {
            if (op > OutSize - 32)
            {
                _outPos = op;
                Drain();
                op = 0;
            }

            int dist = dists[i];
            if (dist == 0)
            {
                uint e = litEnc[lits[i]];
                bb |= (ulong)(e & 0xFFFF) << bc;
                bc += (int)(e >> 16);
            }
            else
            {
                int lm3 = lits[i];
                int ls = lenSym[lm3];
                uint e = litEnc[257 + ls];
                int el = (int)(e >> 16);
                bb |= (ulong)((e & 0xFFFF) | ((uint)(lm3 + MinMatch - lenBase[ls]) << el)) << bc;
                bc += el + lenExtra[ls];
                if (bc >= 32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(op), (uint)bb);
                    op += 4;
                    bb >>= 32;
                    bc -= 32;
                }

                int ds = DeflateTables.DistanceSymbol(dist);
                e = distEnc[ds];
                el = (int)(e >> 16);
                bb |= (ulong)((e & 0xFFFF) | ((uint)(dist - distBase[ds]) << el)) << bc;
                bc += el + distExtra[ds];
            }

            if (bc >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(op), (uint)bb);
                op += 4;
                bb >>= 32;
                bc -= 32;
            }
        }

        uint eob = litEnc[256];
        bb |= (ulong)(eob & 0xFFFF) << bc;
        bc += (int)(eob >> 16);
        if (bc >= 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(op), (uint)bb);
            op += 4;
            bb >>= 32;
            bc -= 32;
        }

        _bitBuf = bb;
        _bitCnt = bc;
        _outPos = op;
    }

    // Run-length codes the concatenated literal and distance code lengths (RFC 1951 3.2.7).
    private int TokenizeLengths(int total)
    {
        byte[] a = _allLens;
        int t = 0;
        int i = 0;
        while (i < total)
        {
            byte cur = a[i];
            int run = 1;
            while (i + run < total && a[i + run] == cur)
            {
                run++;
            }

            i += run;
            if (cur == 0)
            {
                while (run >= 11)
                {
                    int r = Math.Min(run, 138);
                    _tokSym[t] = 18;
                    _tokExtra[t++] = (byte)(r - 11);
                    run -= r;
                }

                if (run >= 3)
                {
                    _tokSym[t] = 17;
                    _tokExtra[t++] = (byte)(run - 3);
                    run = 0;
                }
            }
            else
            {
                _tokSym[t++] = cur;
                run--;
                while (run >= 3)
                {
                    int r = Math.Min(run, 6);
                    _tokSym[t] = 16;
                    _tokExtra[t++] = (byte)(r - 3);
                    run -= r;
                }
            }

            while (run-- > 0)
            {
                _tokSym[t++] = cur;
            }
        }

        return t;
    }

    // ---- Huffman code construction -------------------------------------------------------------

    /// <summary>
    /// Builds length-limited prefix code lengths from frequencies. Always yields a complete code with at
    /// least two symbols so every decoder accepts it. Ties are broken by symbol index for determinism.
    /// </summary>
    private void BuildLengths(ReadOnlySpan<int> freq, Span<byte> lens, int maxBits)
    {
        lens.Clear();
        int n = 0;
        for (int s = 0; s < freq.Length; s++)
        {
            if (freq[s] != 0)
            {
                _keys[n++] = ((ulong)(uint)freq[s] << 16) | (uint)s;
            }
        }

        if (n < 2)
        {
            // A code needs two symbols to be complete: use the one present (if any) plus a dummy.
            int only = n == 1 ? (int)(_keys[0] & 0xFFFF) : 0;
            lens[only] = 1;
            lens[only == 0 ? 1 : 0] = 1;
            return;
        }

        Span<ulong> keys = _keys.AsSpan(0, n);
        keys.Sort();

        // Two-queue Huffman construction: leaves 0..n-1 in ascending weight, internal nodes after them.
        int[] w = _nodeW;
        int[] parent = _nodeP;
        for (int i = 0; i < n; i++)
        {
            w[i] = (int)(keys[i] >> 16);
        }

        int leaf = 0;
        int node = n;
        int next = n;
        for (int k = 0; k < n - 1; k++)
        {
            int a;
            int b;
            if (leaf < n && (node >= next || w[leaf] <= w[node]))
            {
                a = leaf++;
            }
            else
            {
                a = node++;
            }

            if (leaf < n && (node >= next || w[leaf] <= w[node]))
            {
                b = leaf++;
            }
            else
            {
                b = node++;
            }

            w[next] = w[a] + w[b];
            parent[a] = next;
            parent[b] = next;
            next++;
        }

        int[] depth = _depth;
        depth[next - 1] = 0;
        Span<int> numCodes = stackalloc int[maxBits + 2];
        for (int i = next - 2; i >= 0; i--)
        {
            depth[i] = depth[parent[i]] + 1;
        }

        for (int i = 0; i < n; i++)
        {
            numCodes[Math.Min(depth[i], maxBits)]++;
        }

        // Clamping overlong codes oversubscribes the code; repair it by demoting shorter codes.
        long total = 0;
        for (int i = 1; i <= maxBits; i++)
        {
            total += (long)numCodes[i] << (maxBits - i);
        }

        while (total != (1L << maxBits))
        {
            numCodes[maxBits]--;
            for (int i = maxBits - 1; i > 0; i--)
            {
                if (numCodes[i] != 0)
                {
                    numCodes[i]--;
                    numCodes[i + 1] += 2;
                    break;
                }
            }

            total--;
        }

        // Least frequent symbols (start of the sorted order) take the longest codes.
        int cur = 0;
        for (int len = maxBits; len >= 1; len--)
        {
            for (int c = numCodes[len]; c > 0; c--)
            {
                lens[(int)(keys[cur++] & 0xFFFF)] = (byte)len;
            }
        }
    }

    // Canonical codes (RFC 1951 3.2.2), stored bit-reversed with the length in the high half.
    private static void MakeCodes(ReadOnlySpan<byte> lens, uint[] enc)
    {
        Span<int> count = stackalloc int[16];
        for (int i = 0; i < lens.Length; i++)
        {
            count[lens[i]]++;
        }

        count[0] = 0;
        Span<int> next = stackalloc int[16];
        int code = 0;
        for (int l = 1; l < 16; l++)
        {
            code = (code + count[l - 1]) << 1;
            next[l] = code;
        }

        for (int i = 0; i < lens.Length; i++)
        {
            int l = lens[i];
            if (l == 0)
            {
                enc[i] = 0;
                continue;
            }

            int c = next[l]++;
            uint r = 0;
            for (int b = 0; b < l; b++)
            {
                r = (r << 1) | (uint)(c & 1);
                c >>= 1;
            }

            enc[i] = r | ((uint)l << 16);
        }
    }

    private static uint[] BuildFixedLit()
    {
        var lens = new byte[288];
        for (int i = 0; i < 288; i++)
        {
            lens[i] = (byte)FixedLen(i);
        }

        var enc = new uint[288];
        MakeCodes(lens, enc);
        return enc;
    }

    private static uint[] BuildFixedDist()
    {
        var lens = new byte[32];
        Array.Fill(lens, (byte)5);
        var enc = new uint[32];
        MakeCodes(lens, enc);
        return enc;
    }

    // ---- bit output ----------------------------------------------------------------------------

    private void PutBits(uint value, int n)
    {
        _bitBuf |= (ulong)value << _bitCnt;
        _bitCnt += n;
        if (_bitCnt >= 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_out.AsSpan(_outPos), (uint)_bitBuf);
            _outPos += 4;
            _bitBuf >>= 32;
            _bitCnt -= 32;
        }
    }

    private void FlushBitsToByte()
    {
        while (_bitCnt > 0)
        {
            _out[_outPos++] = (byte)_bitBuf;
            _bitBuf >>= 8;
            _bitCnt = Math.Max(0, _bitCnt - 8);
        }

        _bitBuf = 0;
    }

    private void EnsureOut(int bytes)
    {
        if (_outPos > OutSize - bytes - 16)
        {
            Drain();
        }
    }

    private void Drain()
    {
        if (_outPos > 0)
        {
            _sink.Write(_out.AsSpan(0, _outPos));
            _outPos = 0;
        }
    }
}
