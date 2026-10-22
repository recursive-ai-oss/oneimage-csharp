using System;
using System.Buffers;
using System.Buffers.Binary;

namespace OneImage.Compression;

/// <summary>The two LZW bit-packing conventions used by image formats.</summary>
internal enum LzwVariant
{
    /// <summary>GIF: codes packed least-significant bit first, variable minimum code size (2-8),
    /// code width grows when the table fills the current width.</summary>
    Gif,

    /// <summary>TIFF 6.0: codes packed most-significant bit first, 8-bit symbols, clear = 256,
    /// end = 257, and "early change" (the code width grows one code before the table fills it).</summary>
    Tiff,
}

/// <summary>Shared constants for the LZW coder and decoder.</summary>
internal static class Lzw
{
    public const int MaxCodes = 4096;
    public const int MaxBits = 12;

    public static int RootBits(LzwVariant variant, int minCodeSize)
    {
        if (variant == LzwVariant.Tiff)
        {
            return 8;
        }

        if (minCodeSize is < 2 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(minCodeSize), "GIF LZW minimum code size must be 2 to 8.");
        }

        return minCodeSize;
    }
}

/// <summary>
/// Incremental LZW decoder for GIF and TIFF. Feed the concatenated code stream (for GIF, the image data
/// with its sub-block framing already removed). A decoded string that does not fit the output span is
/// held internally, so output can be pulled in any chunk size. Malformed codes throw
/// <see cref="ImageFormatException"/>; exceeding the output cap or expansion ratio throws
/// <see cref="ImageLimitException"/>.
/// </summary>
/// <remarks>
/// <para>
/// When the table is full the decoder keeps decoding with the existing entries until a clear code
/// (the "deferred clear" convention), as GIF and TIFF writers in the wild rely on.
/// </para>
/// <para>
/// Table entries are stored in 8-byte chunks: each entry holds the last 1-8 bytes of its string packed in a
/// <see cref="ulong"/> plus a link to the entry for the string without those bytes (always a multiple of 8 long).
/// Emitting a string then costs one table hop and one 8-byte store per 8 bytes instead of one per byte.
/// The same instance can decode many streams: call <see cref="Reset"/> between them.
/// </para>
/// </remarks>
internal sealed class LzwDecoder
{
    private const long RatioGrace = 1 << 16;

    private readonly LzwVariant _variant;
    private readonly int _early;
    private int _minSize;
    private int _clear;
    private int _eoi;
    private int _first;
    private long _maxOutput;
    private double _maxRatio;

    // One 16-byte record per table entry keeps everything a code needs in a single cache line.
    private struct Entry
    {
        public ulong Tail;
        public short ChunkPrev;
        public ushort Length;
        public byte First;
    }

    private readonly Entry[] _t = new Entry[Lzw.MaxCodes];
    private readonly byte[] _scratch = new byte[Lzw.MaxCodes + 16];
    private int _pendPos;
    private int _pendLen;

    private ulong _bitbuf;
    private int _bitcount;
    private int _codeSize;
    private int _next;
    private int _prev;
    private bool _done;
    private long _total;
    private long _inTotal;

    /// <summary>Creates a decoder.</summary>
    /// <param name="variant">GIF or TIFF packing.</param>
    /// <param name="minCodeSize">GIF minimum code size (2-8); ignored for TIFF.</param>
    /// <param name="maxOutputBytes">Maximum total decoded bytes.</param>
    /// <param name="maxExpansionRatio">Maximum decoded bytes per input byte (plus a 64 KiB allowance); 0 for none.</param>
    public LzwDecoder(LzwVariant variant, int minCodeSize = 8, long maxOutputBytes = long.MaxValue, double maxExpansionRatio = 0)
    {
        _variant = variant;
        _early = variant == LzwVariant.Tiff ? 1 : 0;
        Reset(minCodeSize, maxOutputBytes, maxExpansionRatio);
    }

    /// <summary>Total decoded bytes produced so far.</summary>
    public long TotalOut => _total;

    /// <summary>Starts a new stream with new limits, reusing the tables. The variant stays the same.</summary>
    public void Reset(int minCodeSize = 8, long maxOutputBytes = long.MaxValue, double maxExpansionRatio = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxExpansionRatio);
        _minSize = Lzw.RootBits(_variant, minCodeSize);
        _clear = 1 << _minSize;
        _eoi = _clear + 1;
        _first = _clear + 2;
        _maxOutput = maxOutputBytes;
        _maxRatio = double.IsFinite(maxExpansionRatio) ? maxExpansionRatio : 0;
        for (int i = 0; i < _clear; i++)
        {
            _t[i] = new Entry { Tail = (ulong)i, ChunkPrev = -1, First = (byte)i, Length = 1 };
        }

        _pendPos = 0;
        _pendLen = 0;
        _bitbuf = 0;
        _bitcount = 0;
        _done = false;
        _total = 0;
        _inTotal = 0;
        ResetTable();
    }

    private void ResetTable()
    {
        _codeSize = _minSize + 1;
        _next = _first;
        _prev = -1;
    }

    /// <summary>
    /// Decodes input into output. Status semantics match <see cref="Inflater.Inflate"/>: with
    /// <see cref="InflateStatus.Done"/> (end-of-information code read) bytes not reported consumed follow the data.
    /// </summary>
    public InflateStatus Decode(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written)
    {
        int ip = 0;
        int op = 0;
        consumed = 0;
        written = 0;
        bool msb = _variant == LzwVariant.Tiff;
        long limit = Limit(0);
        while (true)
        {
            if (_pendPos < _pendLen)
            {
                int n = Math.Min(_pendLen - _pendPos, output.Length - op);
                _scratch.AsSpan(_pendPos, n).CopyTo(output.Slice(op));
                _pendPos += n;
                op += n;
                if (_pendPos < _pendLen)
                {
                    consumed = ip;
                    written = op;
                    return InflateStatus.NeedOutput;
                }
            }

            if (_done)
            {
                break;
            }

            // Make sure a whole code is buffered.
            if (_bitcount < _codeSize)
            {
                if (ip <= input.Length - 8)
                {
                    if (msb)
                    {
                        _bitbuf |= BinaryPrimitives.ReadUInt64BigEndian(input.Slice(ip)) >> _bitcount;
                    }
                    else
                    {
                        _bitbuf |= BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(ip)) << _bitcount;
                    }

                    ip += (63 - _bitcount) >> 3;
                    _bitcount |= 56;
                }
                else
                {
                    while (_bitcount < _codeSize && ip < input.Length)
                    {
                        ulong b = input[ip++];
                        _bitbuf |= msb ? b << (56 - _bitcount) : b << _bitcount;
                        _bitcount += 8;
                    }

                    if (_bitcount < _codeSize)
                    {
                        ClearGarbage();
                        _inTotal += ip;
                        consumed = ip;
                        written = op;
                        return InflateStatus.NeedInput;
                    }
                }

                limit = Limit(ip);
            }

            int code;
            if (msb)
            {
                code = (int)(_bitbuf >> (64 - _codeSize));
                _bitbuf <<= _codeSize;
            }
            else
            {
                code = (int)(_bitbuf & ((1UL << _codeSize) - 1));
                _bitbuf >>= _codeSize;
            }

            _bitcount -= _codeSize;

            if (code == _clear)
            {
                ResetTable();
                continue;
            }

            if (code == _eoi)
            {
                _done = true;
                continue;
            }

            if (_prev < 0)
            {
                if (code >= _clear)
                {
                    throw new ImageFormatException("LZW stream does not start with a literal after a clear code.");
                }
            }
            else
            {
                if (code > _next || (code == _next && _next >= Lzw.MaxCodes))
                {
                    throw new ImageFormatException("LZW code refers to an entry that does not exist yet.");
                }

                if (_next < Lzw.MaxCodes)
                {
                    // New entry: the previous string plus the first byte of the current one.
                    int e = _next++;
                    ref Entry p = ref _t[_prev];
                    int lp = p.Length;
                    ulong ch = code < e ? _t[code].First : p.First;
                    ref Entry n = ref _t[e];
                    n.First = p.First;
                    n.Length = (ushort)(lp + 1);
                    if ((lp & 7) == 0)
                    {
                        n.ChunkPrev = (short)_prev;
                        n.Tail = ch;
                    }
                    else
                    {
                        n.ChunkPrev = p.ChunkPrev;
                        n.Tail = p.Tail | (ch << (8 * (((lp - 1) & 7) + 1)));
                    }

                    if (_next + _early == (1 << _codeSize) && _codeSize < Lzw.MaxBits)
                    {
                        _codeSize++;
                    }
                }
            }

            _prev = code;
            int len = _t[code].Length;
            _total += len;
            if (_total > limit)
            {
                ThrowLimit();
            }

            if (output.Length - op >= len + 8)
            {
                Emit(code, len, output.Slice(op));
                op += len;
            }
            else if (output.Length - op >= len)
            {
                // Too close to the end of the output for the 8-byte stores: build in scratch, then copy.
                Emit(code, len, _scratch);
                _scratch.AsSpan(0, len).CopyTo(output.Slice(op));
                op += len;
            }
            else
            {
                Emit(code, len, _scratch);
                _pendPos = 0;
                _pendLen = len;
            }
        }

        // End of information: give back whole bytes that were buffered but not needed.
        int back = Math.Min(_bitcount >> 3, ip);
        ip -= back;
        _bitbuf = 0;
        _bitcount = 0;
        _inTotal += ip;
        consumed = ip;
        written = op;
        return InflateStatus.Done;
    }

    // Writes the string for 'code' so that it ends at dst[len]. The first (partial) chunk stores 8 bytes and so
    // scribbles up to 7 bytes past the string; callers pass a span with that much room.
    private void Emit(int code, int len, Span<byte> dst)
    {
        int pos = len - (((len - 1) & 7) + 1);
        Entry[] t = _t;
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(pos), t[code].Tail);
        int c = t[code].ChunkPrev;
        while (c >= 0)
        {
            pos -= 8;
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(pos), t[c].Tail);
            c = t[c].ChunkPrev;
        }
    }

    // The most output allowed so far: the output cap, or the ratio budget for the input consumed.
    private long Limit(int ip)
    {
        if (_maxRatio <= 0)
        {
            return _maxOutput;
        }

        double budget = (_maxRatio * (_inTotal + ip)) + RatioGrace;
        return budget >= _maxOutput ? _maxOutput : (long)budget;
    }

    private void ThrowLimit()
    {
        if (_total > _maxOutput)
        {
            throw new ImageLimitException("Decompressed data exceeds the configured output limit.");
        }

        throw new ImageLimitException("Decompressed data exceeds the configured expansion ratio.");
    }

    private void ClearGarbage()
    {
        // Bits beyond the valid count are copies of upcoming input; drop them so they cannot be confused
        // with data from a later call.
        if (_bitcount == 0)
        {
            _bitbuf = 0;
        }
        else if (_variant == LzwVariant.Tiff)
        {
            _bitbuf &= ~(ulong.MaxValue >> _bitcount);
        }
        else
        {
            _bitbuf &= (1UL << _bitcount) - 1;
        }
    }
}

/// <summary>
/// LZW encoder for GIF and TIFF. Deterministic: the same input yields the same bytes regardless of how it
/// is split across <see cref="Write"/> calls. A clear code starts the stream and is re-issued when the
/// table fills (GIF: at 4096 entries; TIFF: at 4094, as TIFF 6.0 requires).
/// </summary>
internal sealed class LzwEncoder
{
    private const int OutSize = 4096;
    private const int HashSize = 1 << 14;

    private IBufferWriter<byte> _sink = null!;
    private readonly LzwVariant _variant;
    private int _minSize;
    private int _clear;
    private int _eoi;
    private int _first;
    private readonly int _early;
    private readonly int _fullAt;

    private readonly int[] _keys = new int[HashSize];
    private readonly ushort[] _vals = new ushort[HashSize];
    private readonly byte[] _out = new byte[OutSize];
    private int _outPos;
    private ulong _bitbuf;
    private int _bitcount;

    private int _cur = -1;
    private int _dictNext;
    private int _decNext;
    private int _emitted;
    private int _codeSize;
    private bool _started;
    private bool _finished;

    /// <summary>Creates an encoder writing to <paramref name="sink"/>.</summary>
    /// <param name="sink">Receives the code stream (no GIF sub-block framing).</param>
    /// <param name="variant">GIF or TIFF packing.</param>
    /// <param name="minCodeSize">GIF minimum code size (2-8): bits per pixel index; ignored for TIFF.</param>
    public LzwEncoder(IBufferWriter<byte> sink, LzwVariant variant, int minCodeSize = 8)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _variant = variant;
        _early = variant == LzwVariant.Tiff ? 1 : 0;
        _fullAt = variant == LzwVariant.Tiff ? 4094 : Lzw.MaxCodes;
        Reset(sink, minCodeSize);
    }

    /// <summary>Starts a new stream on the same sink, reusing the tables.</summary>
    /// <param name="minCodeSize">GIF minimum code size (2-8); ignored for TIFF.</param>
    public void Reset(int minCodeSize = 8) => Reset(_sink, minCodeSize);

    /// <summary>Starts a new stream writing to <paramref name="sink"/>, reusing the tables.</summary>
    /// <param name="sink">Receives the code stream.</param>
    /// <param name="minCodeSize">GIF minimum code size (2-8); ignored for TIFF.</param>
    public void Reset(IBufferWriter<byte> sink, int minCodeSize = 8)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _minSize = Lzw.RootBits(_variant, minCodeSize);
        _clear = 1 << _minSize;
        _eoi = _clear + 1;
        _first = _clear + 2;
        _outPos = 0;
        _bitbuf = 0;
        _bitcount = 0;
        _cur = -1;
        _started = false;
        _finished = false;
        ResetTable();
    }

    private void ResetTable()
    {
        Array.Fill(_keys, -1);
        _dictNext = _first;
        _decNext = _first;
        _emitted = 0;
        _codeSize = _minSize + 1;
    }

    /// <summary>Encodes more symbols. For GIF every value must be below 2^minCodeSize.</summary>
    /// <exception cref="ArgumentException">A symbol is out of range for the minimum code size.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The stream is already finished.");
        }

        Start();
        int cur = _cur;
        int limit = _clear;
        for (int i = 0; i < data.Length; i++)
        {
            int b = data[i];
            if (b >= limit)
            {
                _cur = cur;
                throw new ArgumentException("A symbol exceeds the range allowed by the minimum code size.", nameof(data));
            }

            if (cur < 0)
            {
                cur = b;
                continue;
            }

            int key = (cur << 8) | b;
            int h = (int)(((uint)key * 0x9E3779B1u) >> 18);
            while (true)
            {
                int k = _keys[h];
                if (k == key)
                {
                    cur = _vals[h];
                    break;
                }

                if (k < 0)
                {
                    Emit(cur);
                    _keys[h] = key;
                    _vals[h] = (ushort)_dictNext++;
                    if (_dictNext >= _fullAt)
                    {
                        PutCode(_clear);
                        ResetTable();
                    }

                    cur = b;
                    break;
                }

                h = (h + 1) & (HashSize - 1);
            }
        }

        _cur = cur;
    }

    /// <summary>Writes the pending code, the end-of-information code and any padding.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        Start();
        if (_cur >= 0)
        {
            Emit(_cur);
            _cur = -1;
        }

        PutCode(_eoi);
        if (_bitcount > 0)
        {
            if (_variant == LzwVariant.Tiff)
            {
                _out[_outPos++] = (byte)(_bitbuf << (8 - _bitcount));
            }
            else
            {
                _out[_outPos++] = (byte)_bitbuf;
            }

            _bitcount = 0;
            _bitbuf = 0;
        }

        Drain();
        _finished = true;
    }

    private void Start()
    {
        if (!_started)
        {
            _started = true;
            PutCode(_clear);
        }
    }

    // Emits a data code, then mirrors the decoder's table growth so the next code width matches.
    private void Emit(int code)
    {
        PutCode(code);
        if (_emitted > 0 && _decNext < Lzw.MaxCodes)
        {
            _decNext++;
            if (_decNext + _early == (1 << _codeSize) && _codeSize < Lzw.MaxBits)
            {
                _codeSize++;
            }
        }

        _emitted++;
    }

    private void PutCode(int code)
    {
        if (_variant == LzwVariant.Tiff)
        {
            _bitbuf = (_bitbuf << _codeSize) | (uint)code;
            _bitcount += _codeSize;
            while (_bitcount >= 8)
            {
                _out[_outPos++] = (byte)(_bitbuf >> (_bitcount - 8));
                _bitcount -= 8;
            }
        }
        else
        {
            _bitbuf |= (ulong)code << _bitcount;
            _bitcount += _codeSize;
            while (_bitcount >= 8)
            {
                _out[_outPos++] = (byte)_bitbuf;
                _bitbuf >>= 8;
                _bitcount -= 8;
            }
        }

        if (_outPos > OutSize - 8)
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

    /// <summary>Compresses a whole buffer in one call.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, LzwVariant variant, int minCodeSize = 8)
    {
        var sink = new ArrayBufferWriter<byte>(Math.Max(64, data.Length / 2));
        var e = new LzwEncoder(sink, variant, minCodeSize);
        e.Write(data);
        e.Finish();
        return sink.WrittenSpan.ToArray();
    }
}
