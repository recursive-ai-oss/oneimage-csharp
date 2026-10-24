using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OneImage.Codecs.WebP;

/// <summary>Receives decoded VP8L rows as 0xAARRGGBB pixels, top to bottom.</summary>
internal interface IArgbRowSink
{
    /// <summary>Delivers one finished row of the image.</summary>
    void WriteRow(int y, ReadOnlySpan<uint> argb);
}

/// <summary>
/// Decoder for the WebP lossless bitstream (RFC 9649 section 3). Lossless decoding is bit-exact. The whole entropy-coded
/// pixel array is held in memory because backward references may reach 2^20 pixels back; inverse transforms then run row by row
/// into the sink.
/// </summary>
internal sealed class VP8LDecoder : IDisposable
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int MaxCacheBits = 11;
    private const int MaxAlphabet = NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits);
    private const int ColorCacheMul = 0x1e35a7bd;

    private static readonly byte[] CodeLengthOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // (xi, yi) pairs of the 120 short distance codes, RFC 9649 figure 20.
    private static readonly sbyte[] DistanceMap =
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    private readonly byte[] _data;
    private readonly int _end;
    private readonly ImageLimits _limits;
    private readonly CancellationToken _token;
    private readonly List<uint[]> _rented = [];

    private ulong _val;
    private int _nbits;
    private int _pos;
    private int _overrun;

    private uint[] _arena = new uint[4096];
    private int _arenaUsed;

    private Transform[] _transforms = new Transform[4];
    private int _transformCount;
    private int _transformMask;

    /// <summary>Initializes a decoder over a VP8L bitstream held in <paramref name="data"/>.</summary>
    public VP8LDecoder(byte[] data, int offset, int length, ImageLimits limits, CancellationToken token)
    {
        _data = data;
        _pos = offset;
        _end = offset + length;
        _limits = limits;
        _token = token;
    }

    private enum TransformKind
    {
        Predictor = 0,
        Color = 1,
        SubtractGreen = 2,
        ColorIndexing = 3,
    }

    private bool Eos => _overrun > 0 && _nbits < _overrun * 8;

    /// <summary>Reads the 5-byte header of a VP8L chunk.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out int width, out int height, out bool alphaHint)
    {
        width = height = 0;
        alphaHint = false;
        if (data.Length < 5 || data[0] != 0x2f)
        {
            return false;
        }

        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
        width = (int)(bits & 0x3fff) + 1;
        height = (int)((bits >> 14) & 0x3fff) + 1;
        alphaHint = ((bits >> 28) & 1) != 0;
        return (bits >> 29) == 0;
    }

    /// <summary>Decodes a complete VP8L chunk payload (with header) and delivers its rows.</summary>
    public void DecodeWithHeader(IArgbRowSink sink, out int width, out int height)
    {
        if (!TryReadHeader(_data.AsSpan(_pos, _end - _pos), out width, out height, out _))
        {
            throw new ImageFormatException("Invalid VP8L header.");
        }

        _pos += 5;
        _limits.CheckFrame(width, height);
        Decode(width, height, sink);
    }

    /// <summary>Decodes a headerless VP8L stream of known size (the alpha plane of a lossy file).</summary>
    public void DecodeHeaderless(int width, int height, IArgbRowSink sink) => Decode(width, height, sink);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (uint[] a in _rented)
        {
            Pools<uint>.Pool.Return(a);
        }

        _rented.Clear();
    }

    // ---- bit reader ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill(byte[] data, ref int pos, int end, ref ulong val, ref int nbits, ref int overrun)
    {
        if (pos + 8 <= end)
        {
            val |= BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)) << nbits;
            int adv = (63 - nbits) >> 3;
            pos += adv;
            nbits += adv << 3;
        }
        else
        {
            (val, nbits, pos, overrun) = FillSlow(data, pos, end, val, nbits, overrun);
        }
    }

    // Near the end of the data: byte by byte, zero-filling past the end. Works on copies and returns the new state, so the
    // callers' locals are never address-exposed and the hot loop keeps them in registers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ulong Val, int NBits, int Pos, int Overrun) FillSlow(byte[] data, int pos, int end, ulong val, int nbits, int overrun)
    {
        while (nbits <= 56)
        {
            ulong b = 0;
            if (pos < end)
            {
                b = data[pos++];
            }
            else
            {
                overrun++;
                if (overrun > 64)
                {
                    // Reading far past the end: the stream is truncated; stop supplying bits.
                    throw new ImageFormatException("Truncated VP8L data.");
                }
            }

            val |= b << nbits;
            nbits += 8;
        }

        return (val, nbits, pos, overrun);
    }

    private uint ReadBits(int n)
    {
        if (_nbits < 32)
        {
            Fill(_data, ref _pos, _end, ref _val, ref _nbits, ref _overrun);
        }

        uint v = (uint)(_val & ((1UL << n) - 1));
        _val >>= n;
        _nbits -= n;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSymbol(uint[] arena, int tbl, ref ulong val, ref int nbits)
    {
        uint e = arena[tbl + (int)(val & 0xff)];
        int b = (int)(e >> 16);
        if (b > HuffmanTable.RootBits)
        {
            int rootIdx = tbl + (int)(val & 0xff);
            val >>= HuffmanTable.RootBits;
            nbits -= HuffmanTable.RootBits;
            e = arena[rootIdx + (int)(e & 0xffff) + (int)(val & ((1u << (b - HuffmanTable.RootBits)) - 1))];
            b = (int)(e >> 16);
        }

        val >>= b;
        nbits -= b;
        return (int)(e & 0xffff);
    }

    private void CheckEos()
    {
        if (Eos)
        {
            throw new ImageFormatException("Truncated VP8L data.");
        }
    }

    // ---- stream structure ----

    private void Decode(int width, int height, IArgbRowSink sink)
    {
        _arenaUsed = 0;
        int xsize = width;
        // Transforms (section 3.5): each type at most once; later ones see the width left by color indexing.
        while (ReadBits(1) != 0)
        {
            var kind = (TransformKind)ReadBits(2);
            if ((_transformMask & (1 << (int)kind)) != 0)
            {
                throw new ImageFormatException("A VP8L transform is used twice.");
            }

            _transformMask |= 1 << (int)kind;
            var t = new Transform { Kind = kind, XSize = xsize, YSize = height };
            switch (kind)
            {
                case TransformKind.Predictor:
                case TransformKind.Color:
                    t.Bits = (int)ReadBits(3) + 2;
                    t.Data = DecodeImageStream(DivRoundUp(xsize, 1 << t.Bits), DivRoundUp(height, 1 << t.Bits), isLevel0: false, out _);
                    break;
                case TransformKind.ColorIndexing:
                    {
                        int numColors = (int)ReadBits(8) + 1;
                        t.Bits = numColors > 16 ? 0 : numColors > 4 ? 1 : numColors > 2 ? 2 : 3;
                        uint[] pal = DecodeImageStream(numColors, 1, isLevel0: false, out _);
                        int full = 1 << (8 >> t.Bits);
                        uint[] table = Rent(Math.Max(full, 256));
                        Array.Clear(table, 0, table.Length);
                        uint prev = 0;
                        for (int i = 0; i < numColors; i++)
                        {
                            prev = AddPixels(pal[i], prev);
                            table[i] = prev;
                        }

                        t.Data = table;
                        xsize = DivRoundUp(xsize, 1 << t.Bits);
                        break;
                    }

                default:
                    break;
            }

            _transforms[_transformCount++] = t;
            CheckEos();
        }

        _sink = sink;
        _height0 = height;
        _outRows = new uint[_transformCount + 1][];
        _prevRows = new uint[_transformCount][];
        for (int i = 0; i < _transformCount; i++)
        {
            _outRows[i] = Rent(_transforms[i].XSize);
            if (_transforms[i].Kind == TransformKind.Predictor)
            {
                _prevRows[i] = Rent(_transforms[i].XSize);
            }
        }

        try
        {
            DecodeImageStream(xsize, height, isLevel0: true, out _);
            _pipe?.Finish();
        }
        finally
        {
            _pipe?.Dispose();
            _pipe = null;
        }

        CheckEos();
    }

    // Reads (optionally) a color cache, meta prefix codes and the entropy-coded pixels of one image.
    private uint[] DecodeImageStream(int xsize, int ysize, bool isLevel0, out int width)
    {
        width = xsize;
        int cacheBits = 0;
        if (ReadBits(1) != 0)
        {
            cacheBits = (int)ReadBits(4);
            if (cacheBits is < 1 or > MaxCacheBits)
            {
                throw new ImageFormatException("Invalid VP8L color cache size.");
            }
        }

        // Prefix code groups (section 3.7.2.2).
        int prefixBits = 0;
        uint[]? entropyImage = null;
        int prefixWidth = 0;
        int numGroupsMax = 1;
        if (isLevel0 && ReadBits(1) != 0)
        {
            prefixBits = (int)ReadBits(3) + 2;
            prefixWidth = DivRoundUp(xsize, 1 << prefixBits);
            entropyImage = DecodeImageStream(prefixWidth, DivRoundUp(ysize, 1 << prefixBits), isLevel0: false, out _);
            int n = prefixWidth * DivRoundUp(ysize, 1 << prefixBits);
            for (int i = 0; i < n; i++)
            {
                entropyImage[i] = (entropyImage[i] >> 8) & 0xffff;
                numGroupsMax = Math.Max(numGroupsMax, (int)entropyImage[i] + 1);
            }
        }

        // Only groups that some block uses get tables; the others are parsed and dropped.
        int[] groupMap = new int[numGroupsMax];
        int numGroups = 0;
        if (entropyImage == null)
        {
            groupMap[0] = 0;
            numGroups = 1;
        }
        else
        {
            Array.Fill(groupMap, -1);
            int n = prefixWidth * DivRoundUp(ysize, 1 << prefixBits);
            for (int i = 0; i < n; i++)
            {
                ref int m = ref groupMap[entropyImage[i]];
                if (m < 0)
                {
                    m = numGroups++;
                }
            }
        }

        int greenAlphabet = NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        int[] bases = new int[numGroups * 5];
        Span<byte> lengths = stackalloc byte[MaxAlphabet];
        int arenaPos = _arenaUsed;
        for (int g = 0; g < numGroupsMax; g++)
        {
            int slot = groupMap[g];
            for (int k = 0; k < 5; k++)
            {
                int alphabet = k == 0 ? greenAlphabet : k == 4 ? NumDistanceCodes : NumLiteralCodes;
                // Code order within a group: green, red, blue, alpha, distance.
                ReadCodeLengths(lengths[..alphabet]);
                if (slot < 0)
                {
                    continue;
                }

                int size = HuffmanTable.Build(lengths[..alphabet], ref _arena, arenaPos);
                if (size == 0)
                {
                    throw new ImageFormatException("Invalid VP8L prefix code.");
                }

                bases[(slot * 5) + k] = arenaPos;
                arenaPos += size;
            }

            CheckEos();
        }

        _arenaUsed = arenaPos;
        return DecodePixels(xsize, ysize, cacheBits, bases, groupMap, entropyImage, prefixBits, prefixWidth, isLevel0);
    }

    private void ReadCodeLengths(Span<byte> lengths)
    {
        lengths.Clear();
        if (ReadBits(1) != 0)
        {
            // Simple code: one or two symbols of length 1.
            int numSymbols = (int)ReadBits(1) + 1;
            int first8 = (int)ReadBits(1);
            int s0 = (int)ReadBits(first8 == 0 ? 1 : 8);
            if (s0 >= lengths.Length)
            {
                throw new ImageFormatException("Invalid VP8L prefix code.");
            }

            lengths[s0] = 1;
            if (numSymbols == 2)
            {
                int s1 = (int)ReadBits(8);
                if (s1 >= lengths.Length)
                {
                    throw new ImageFormatException("Invalid VP8L prefix code.");
                }

                lengths[s1] = 1;
            }

            return;
        }

        Span<byte> clLengths = stackalloc byte[19];
        int numCodes = (int)ReadBits(4) + 4;
        for (int i = 0; i < numCodes; i++)
        {
            clLengths[CodeLengthOrder[i]] = (byte)ReadBits(3);
        }

        uint[] clTable = new uint[1 << 7];
        // The code-length code is at most 7 bits long, so a root-only table of 256 entries is enough.
        uint[] tmp = new uint[256];
        if (HuffmanTable.Build(clLengths, ref tmp, 0) == 0)
        {
            throw new ImageFormatException("Invalid VP8L code length code.");
        }

        _ = clTable;
        int maxSymbol = lengths.Length;
        if (ReadBits(1) != 0)
        {
            int lengthBits = 2 + (2 * (int)ReadBits(3));
            maxSymbol = 2 + (int)ReadBits(lengthBits);
            if (maxSymbol > lengths.Length)
            {
                throw new ImageFormatException("Invalid VP8L prefix code.");
            }
        }

        int symbol = 0;
        int prevLen = 8;
        while (symbol < lengths.Length)
        {
            if (maxSymbol-- == 0)
            {
                break;
            }

            if (_nbits < 32)
            {
                Fill(_data, ref _pos, _end, ref _val, ref _nbits, ref _overrun);
            }

            int codeLen = ReadSymbol(tmp, 0, ref _val, ref _nbits);
            if (codeLen < 16)
            {
                lengths[symbol++] = (byte)codeLen;
                if (codeLen != 0)
                {
                    prevLen = codeLen;
                }
            }
            else
            {
                int slot = codeLen - 16;
                int extra = slot == 0 ? 2 : slot == 1 ? 3 : 7;
                int offset = slot == 2 ? 11 : 3;
                int repeat = (int)ReadBits(extra) + offset;
                if (symbol + repeat > lengths.Length)
                {
                    throw new ImageFormatException("Invalid VP8L code length repeat.");
                }

                byte v = codeLen == 16 ? (byte)prevLen : (byte)0;
                lengths.Slice(symbol, repeat).Fill(v);
                symbol += repeat;
            }
        }

        CheckEos();
    }

    private uint[] DecodePixels(int width, int height, int cacheBits, int[] bases, int[] groupMap, uint[]? entropy, int prefixBits, int prefixWidth, bool level0)
    {
        uint[] arena = _arena;
        ulong val = _val;
        int nbits = _nbits;
        int pos = _pos;
        int overrun = _overrun;
        byte[] src = _data;
        int end = _end;

        int total = checked(width * height);
        // Memory follows the data actually decoded, not the header: the pixel buffer starts small and grows (sub-images), or
        // slides over a window of the last 2^20 pixels while finished rows go through the inverse transforms (the main image).
        uint[] data = Rent(Math.Min(total, 1 << 16));
        int baseAbs = 0;
        int emitted = 0;
        _pWidth = width;
        _pTotal = total;
        _pLevel0 = level0;
        int next = NextCheck(data.Length, 0, 0, 0, total, width, level0);
        int p = 0;
        int x = 0;
        int y = 0;
        int lastCached = 0;
        uint[]? cache = null;
        int lim = total;
        if (cacheBits > 0)
        {
            cache = Rent(1 << cacheBits);
            Array.Clear(cache, 0, 1 << cacheBits);
        }

        int cacheShift = 32 - cacheBits;
        int mask = entropy == null ? -1 : (1 << prefixBits) - 1;
        int gBase = 0;
        int rBase = 0;
        int bBase = 0;
        int aBase = 0;
        int dBase = 0;
        // A group whose red, blue and alpha codes each have a single symbol (typical for opaque photos: alpha is always 255)
        // needs only the green code per literal; the other three channels are constants. Bit 32 flags such groups.
        int groups = bases.Length / 5;
        long[] trivialArgb = new long[groups];
        for (int gi = 0; gi < groups; gi++)
        {
            uint re = arena[bases[(gi * 5) + 1]];
            uint be = arena[bases[(gi * 5) + 2]];
            uint ae = arena[bases[(gi * 5) + 3]];
            long flags = (ae >> 16) == 0 ? (1L << 33) | ((long)(ae & 0xff) << 40) : 0;
            trivialArgb[gi] = ((re | be | ae) >> 16) == 0 ? flags | (1L << 32) | ((ae & 0xff) << 24) | ((re & 0xff) << 16) | (be & 0xff) : flags;
        }

        long trivial = 0;
        if (entropy == null)
        {
            trivial = trivialArgb[0];
            gBase = bases[0];
            rBase = bases[1];
            bBase = bases[2];
            aBase = bases[3];
            dBase = bases[4];
        }

        while (p < lim)
        {
            if (p >= next)
            {
                _pData = data;
                _pBase = baseAbs;
                _pEmitted = emitted;
                _pP = p;
                _pLast = lastCached;
                _pY = y;
                _pCache = cache;
                _pShift = cacheShift;
                ServicePixels();
                data = _pData;
                baseAbs = _pBase;
                emitted = _pEmitted;
                p = _pP;
                lastCached = _pLast;
                lim = total - baseAbs;
                next = NextCheck(data.Length, p, baseAbs, emitted, total, width, level0);
            }

            if ((x & mask) == 0 && entropy != null)
            {
                int gi = groupMap[entropy[((y >> prefixBits) * prefixWidth) + (x >> prefixBits)]];
                int g = gi * 5;
                trivial = trivialArgb[gi];
                gBase = bases[g];
                rBase = bases[g + 1];
                bBase = bases[g + 2];
                aBase = bases[g + 3];
                dBase = bases[g + 4];
            }

            if (nbits < 32)
            {
                Fill(src, ref pos, end, ref val, ref nbits, ref overrun);
            }

            int code = ReadSymbol(arena, gBase, ref val, ref nbits);
            if (code < NumLiteralCodes)
            {
                if ((trivial & (1L << 32)) != 0)
                {
                    data[p++] = (uint)trivial | ((uint)code << 8);
                    if (++x >= width)
                    {
                        x = 0;
                        if ((++y & 15) == 0)
                        {
                            if (overrun > 0 && nbits < overrun * 8)
                            {
                                throw new ImageFormatException("Truncated VP8L data.");
                            }

                            _token.ThrowIfCancellationRequested();
                        }
                    }

                    continue;
                }

                int r = ReadSymbol(arena, rBase, ref val, ref nbits);
                if (nbits < 32)
                {
                    Fill(src, ref pos, end, ref val, ref nbits, ref overrun);
                }

                int b = ReadSymbol(arena, bBase, ref val, ref nbits);
                int a = (trivial & (1L << 33)) != 0 ? (int)(trivial >> 40) : ReadSymbol(arena, aBase, ref val, ref nbits);
                data[p++] = ((uint)a << 24) | ((uint)r << 16) | ((uint)code << 8) | (uint)b;
                if (++x >= width)
                {
                    x = 0;
                    if ((++y & 15) == 0)
                    {
                        if (overrun > 0 && nbits < overrun * 8)
                        {
                            throw new ImageFormatException("Truncated VP8L data.");
                        }

                        _token.ThrowIfCancellationRequested();
                    }
                }
            }
            else if (code < NumLiteralCodes + NumLengthCodes)
            {
                int lenSym = code - NumLiteralCodes;
                int length = PrefixValue(lenSym, ref val, ref nbits, src, ref pos, end, ref overrun);
                if (nbits < 32)
                {
                    Fill(src, ref pos, end, ref val, ref nbits, ref overrun);
                }

                int distSym = ReadSymbol(arena, dBase, ref val, ref nbits);
                int distCode = PrefixValue(distSym, ref val, ref nbits, src, ref pos, end, ref overrun);
                int dist = DistanceFromCode(distCode, width);
                if (dist > baseAbs + p || length > lim - p)
                {
                    throw new ImageFormatException("Invalid VP8L backward reference.");
                }

                if (dist >= length)
                {
                    Array.Copy(data, p - dist, data, p, length);
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        data[p + i] = data[p + i - dist];
                    }
                }

                p += length;
                x += length;
                while (x >= width)
                {
                    x -= width;
                    y++;
                    if ((y & 15) == 0)
                    {
                        if (overrun > 0 && nbits < overrun * 8)
                        {
                            throw new ImageFormatException("Truncated VP8L data.");
                        }

                        _token.ThrowIfCancellationRequested();
                    }
                }

                if (p < lim && entropy != null && (x & mask) != 0)
                {
                    // Landed in the middle of a block: the group of that block applies.
                    int gi = groupMap[entropy[((y >> prefixBits) * prefixWidth) + (x >> prefixBits)]];
                    int g = gi * 5;
                    trivial = trivialArgb[gi];
                    gBase = bases[g];
                    rBase = bases[g + 1];
                    bBase = bases[g + 2];
                    aBase = bases[g + 3];
                    dBase = bases[g + 4];
                }

                if (cache != null)
                {
                    // Cache fill is lazy: insert everything produced since the last use.
                    while (lastCached < p)
                    {
                        uint c = data[lastCached++];
                        cache[(int)((c * ColorCacheMul) >> cacheShift)] = c;
                    }
                }
            }
            else
            {
                int key = code - (NumLiteralCodes + NumLengthCodes);
                if (cache == null || key >= (1 << cacheBits))
                {
                    throw new ImageFormatException("Invalid VP8L color cache index.");
                }

                while (lastCached < p)
                {
                    uint c = data[lastCached++];
                    cache[(int)((c * ColorCacheMul) >> cacheShift)] = c;
                }

                data[p++] = cache[key];
                if (++x >= width)
                {
                    x = 0;
                    if ((++y & 15) == 0)
                    {
                        if (overrun > 0 && nbits < overrun * 8)
                        {
                            throw new ImageFormatException("Truncated VP8L data.");
                        }

                        _token.ThrowIfCancellationRequested();
                    }
                }
            }
        }

        _val = val;
        _nbits = nbits;
        _pos = pos;
        _overrun = overrun;
        CheckEos();
        if (level0)
        {
            _pData = data;
            _pBase = baseAbs;
            _pEmitted = emitted;
            _pY = height;
            EmitCompleted();
            return data;
        }

        // A sub-image is read whole; the buffer may be larger than the image.
        return data;
    }

    private const int MaxRun = 4096;
    private const int MaxDistance = 1 << 20;

    private IArgbRowSink _sink = null!;
    private uint[][] _outRows = [];
    private uint[][] _prevRows = [];
    private uint[] _pData = [];
    private uint[]? _pCache;
    private int _pWidth;
    private int _pTotal;
    private int _pBase;
    private int _pEmitted;
    private int _pP;
    private int _pLast;
    private int _pY;
    private int _pShift;
    private bool _pLevel0;

    // The next buffer position at which the decode loop must stop and call ServicePixels.
    private static int NextCheck(int len, int p, int baseAbs, int emitted, int total, int width, bool level0)
    {
        int next = int.MaxValue;
        if ((long)baseAbs + len < total)
        {
            next = len - MaxRun;
        }

        if (level0)
        {
            next = (int)Math.Min(next, ((long)emitted + 16) * width - baseAbs);
        }

        return Math.Max(next, p + 1);
    }

    private void EmitCompleted()
    {
        int width = _pWidth;
        // Large images run the inverse transforms and the sink on a second thread while this one keeps decoding entropy-coded
        // pixels; the output is the same rows in the same order.
        if (_pipe == null && _pEmitted < _pY && (long)width * _height0 >= PipelineMinPixels && Environment.ProcessorCount > 1)
        {
            _pipe = new RowPipe(this, width);
        }

        for (; _pEmitted < _pY; _pEmitted++)
        {
            if ((_pEmitted & 15) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<uint> row = _pData.AsSpan((int)((long)_pEmitted * width - _pBase), width);
            if (_pipe != null)
            {
                _pipe.Add(_pEmitted, row);
            }
            else
            {
                ProcessRow(_pEmitted, row);
            }
        }
    }

    private const int PipelineMinPixels = 1 << 17;
    private RowPipe? _pipe;
    private int _height0;

    // Hands finished rows, 16 at a time, to a worker thread that runs them through the inverse transforms into the sink.
    private sealed class RowPipe : IDisposable
    {
        private const int BatchRows = 16;
        private const int Slots = 3;

        private readonly VP8LDecoder _d;
        private readonly int _w;
        private readonly uint[][] _buf = new uint[Slots][];
        private readonly int[] _start = new int[Slots];
        private readonly int[] _count = new int[Slots];
        private readonly SemaphoreSlim _free = new(Slots, Slots);
        private readonly SemaphoreSlim _filled = new(0);
        private readonly CancellationTokenSource _stop;
        private readonly Task _task;
        private Exception? _error;
        private int _finalBatches = int.MaxValue;
        private int _seq;
        private int _cur;
        private int _rows;

        public RowPipe(VP8LDecoder d, int width)
        {
            _d = d;
            _w = width;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(d._token);
            for (int i = 0; i < Slots; i++)
            {
                _buf[i] = Pools<uint>.Pool.Rent(checked(width * BatchRows));
            }

            _task = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Add(int y, ReadOnlySpan<uint> row)
        {
            if (_rows == 0)
            {
                _free.Wait(_d._token);
                ThrowIfFailed();
                _cur = _seq % Slots;
                _start[_cur] = y;
            }

            row.CopyTo(_buf[_cur].AsSpan(_rows * _w, _w));
            if (++_rows == BatchRows)
            {
                Publish();
            }
        }

        public void Finish()
        {
            if (_rows > 0)
            {
                Publish();
            }

            Volatile.Write(ref _finalBatches, _seq);
            _filled.Release();
            _task.Wait();
            ThrowIfFailed();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _task.Wait();
            _filled.Dispose();
            _free.Dispose();
            _stop.Dispose();
            foreach (uint[] b in _buf)
            {
                Pools<uint>.Pool.Return(b);
            }
        }

        private void Publish()
        {
            _count[_cur] = _rows;
            _rows = 0;
            _seq++;
            _filled.Release();
        }

        private void ThrowIfFailed()
        {
            if (Volatile.Read(ref _error) is Exception e)
            {
                // The worker's exception is rethrown unchanged on the decoding thread.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
            }
        }

        private void Work()
        {
            try
            {
                for (int k = 0; ; k++)
                {
                    _filled.Wait(_stop.Token);
                    if (k >= Volatile.Read(ref _finalBatches))
                    {
                        return;
                    }

                    int slot = k % Slots;
                    _d._token.ThrowIfCancellationRequested();
                    for (int r = 0; r < _count[slot]; r++)
                    {
                        _d.ProcessRow(_start[slot] + r, _buf[slot].AsSpan(r * _w, _w));
                    }

                    _free.Release();
                }
            }
            catch (Exception e)
            {
                // Handed to the decoding thread, which rethrows it (or is already unwinding).
                Volatile.Write(ref _error, e);
                _free.Release(Slots);
            }
        }
    }

    // Emits finished rows, then makes room for at least MaxRun more pixels by sliding the window or growing the buffer.
    private void ServicePixels()
    {
        if (_pLevel0)
        {
            EmitCompleted();
        }

        if (_pP + MaxRun <= _pData.Length || (long)_pBase + _pData.Length >= _pTotal)
        {
            return;
        }

        uint[] data = _pData;
        if (_pLevel0)
        {
            long keepFrom = Math.Min((long)_pEmitted * _pWidth, (long)_pBase + _pP - MaxDistance);
            int drop = (int)Math.Max(0, keepFrom - _pBase);
            if (drop >= data.Length / 4)
            {
                if (_pCache != null)
                {
                    while (_pLast < _pP)
                    {
                        uint c = data[_pLast++];
                        _pCache[(int)((c * ColorCacheMul) >> _pShift)] = c;
                    }
                }

                Array.Copy(data, drop, data, 0, _pP - drop);
                _pP -= drop;
                _pLast -= drop;
                _pBase += drop;
                return;
            }
        }

        int newLen = (int)Math.Min((long)data.Length * 2, _pTotal - (long)_pBase);
        uint[] bigger = Rent(newLen);
        Array.Copy(data, bigger, _pP);
        _rented.Remove(data);
        Pools<uint>.Pool.Return(data);
        _pData = bigger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PrefixValue(int sym, ref ulong val, ref int nbits, byte[] src, ref int pos, int end, ref int overrun)
    {
        if (sym < 4)
        {
            return sym + 1;
        }

        int extra = (sym - 2) >> 1;
        int offset = (2 + (sym & 1)) << extra;
        if (nbits < 32)
        {
            Fill(src, ref pos, end, ref val, ref nbits, ref overrun);
        }

        int v = (int)(val & ((1UL << extra) - 1));
        val >>= extra;
        nbits -= extra;
        return offset + v + 1;
    }

    private static int DistanceFromCode(int code, int xsize)
    {
        if (code > 120)
        {
            return code - 120;
        }

        int xi = DistanceMap[(code - 1) * 2];
        int yi = DistanceMap[((code - 1) * 2) + 1];
        int dist = xi + (yi * xsize);
        return dist < 1 ? 1 : dist;
    }

    // ---- inverse transforms ----

    // Runs one finished row through the inverse transforms, last transform first, and hands it to the sink.
    private void ProcessRow(int y, ReadOnlySpan<uint> row)
    {
        for (int i = _transformCount - 1; i >= 0; i--)
        {
            Transform t = _transforms[i];
            Span<uint> dst = _outRows[i].AsSpan(0, t.XSize);
            switch (t.Kind)
            {
                case TransformKind.Predictor:
                    InversePredictor(t, row, dst, _prevRows[i].AsSpan(0, t.XSize), y);
                    // This row is the next row's "row above": swap the buffers instead of copying. The swapped-out buffer is only
                    // read through `dst` until the sink has the row, which happens before the next call.
                    (_outRows[i], _prevRows[i]) = (_prevRows[i], _outRows[i]);
                    break;
                case TransformKind.Color:
                    InverseColor(t, row, dst, y);
                    break;
                case TransformKind.SubtractGreen:
                    for (int x = 0; x < dst.Length; x++)
                    {
                        uint p = row[x];
                        uint g = (p >> 8) & 0xff;
                        dst[x] = (p & 0xff00ff00) | ((((p & 0xff0000) + (g << 16)) & 0xff0000) | (((p & 0xff) + g) & 0xff));
                    }

                    break;
                default:
                    InverseColorIndexing(t, row, dst);
                    break;
            }

            row = dst;
        }

        _sink.WriteRow(y, row);
    }

    private static void InverseColorIndexing(Transform t, ReadOnlySpan<uint> src, Span<uint> dst)
    {
        uint[] pal = t.Data!;
        if (t.Bits == 0)
        {
            for (int x = 0; x < dst.Length; x++)
            {
                dst[x] = pal[(src[x] >> 8) & 0xff];
            }

            return;
        }

        int bitsPerPixel = 8 >> t.Bits;
        int pixelsPerByte = 1 << t.Bits;
        uint idxMask = (1u << bitsPerPixel) - 1;
        int xo = 0;
        for (int sx = 0; xo < dst.Length; sx++)
        {
            uint packed = (src[sx] >> 8) & 0xff;
            for (int k = 0; k < pixelsPerByte && xo < dst.Length; k++)
            {
                dst[xo++] = pal[(int)(packed & idxMask)];
                packed >>= bitsPerPixel;
            }
        }
    }

    private static void InverseColor(Transform t, ReadOnlySpan<uint> src, Span<uint> dst, int y)
    {
        int tw = DivRoundUp(t.XSize, 1 << t.Bits);
        ReadOnlySpan<uint> elems = t.Data.AsSpan((y >> t.Bits) * tw, tw);
        int w = dst.Length;
        int blockW = 1 << t.Bits;
        for (int bx = 0; bx * blockW < w; bx++)
        {
            uint cte = elems[bx];
            int g2r = (sbyte)(cte & 0xff);
            int g2b = (sbyte)((cte >> 8) & 0xff);
            int r2b = (sbyte)((cte >> 16) & 0xff);
            int xEnd = Math.Min(w, (bx + 1) * blockW);
            for (int x = bx * blockW; x < xEnd; x++)
            {
                uint p = src[x];
                int green = (sbyte)((p >> 8) & 0xff);
                int red = (int)((p >> 16) & 0xff);
                int blue = (int)(p & 0xff);
                red += (g2r * green) >> 5;
                red &= 0xff;
                blue += (g2b * green) >> 5;
                blue += (r2b * (sbyte)red) >> 5;
                blue &= 0xff;
                dst[x] = (p & 0xff00ff00) | ((uint)red << 16) | (uint)blue;
            }
        }
    }

    private static void InversePredictor(Transform t, ReadOnlySpan<uint> src, Span<uint> dst, ReadOnlySpan<uint> prev, int y)
    {
        int w = dst.Length;
        if (y == 0)
        {
            dst[0] = AddPixels(src[0], 0xff000000);
            for (int x = 1; x < w; x++)
            {
                dst[x] = AddPixels(src[x], dst[x - 1]);
            }

            return;
        }

        dst[0] = AddPixels(src[0], prev[0]);
        int tw = DivRoundUp(t.XSize, 1 << t.Bits);
        ReadOnlySpan<uint> modes = t.Data.AsSpan((y >> t.Bits) * tw, tw);
        int blockW = 1 << t.Bits;
        int xs = 1;
        while (xs < w)
        {
            int bx = xs >> t.Bits;
            int xe = Math.Min(w, (bx + 1) * blockW);
            int mode = (int)((modes[bx] >> 8) & 0xf);
            PredictRun(mode, src, dst, prev, xs, xe);
            xs = xe;
        }
    }

    private static void PredictRun(int mode, ReadOnlySpan<uint> src, Span<uint> dst, ReadOnlySpan<uint> prev, int xs, int xe)
    {
        switch (mode)
        {
            case 1: Run<PLeft>(src, dst, prev, xs, xe); break;
            case 2: Run<PTop>(src, dst, prev, xs, xe); break;
            case 3: Run<PTopRight>(src, dst, prev, xs, xe); break;
            case 4: Run<PTopLeft>(src, dst, prev, xs, xe); break;
            case 5: Run<PAvg3>(src, dst, prev, xs, xe); break;
            case 6: Run<PAvgLTl>(src, dst, prev, xs, xe); break;
            case 7: Run<PAvgLT>(src, dst, prev, xs, xe); break;
            case 8: Run<PAvgTlT>(src, dst, prev, xs, xe); break;
            case 9: Run<PAvgTTr>(src, dst, prev, xs, xe); break;
            case 10: Run<PAvg4>(src, dst, prev, xs, xe); break;
            case 11: Run<PSelect>(src, dst, prev, xs, xe); break;
            case 12: Run<PGradient>(src, dst, prev, xs, xe); break;
            case 13: Run<PHalfGradient>(src, dst, prev, xs, xe); break;
            default: Run<PBlack>(src, dst, prev, xs, xe); break;
        }
    }

    // One predictor over pixels xs..xe-1 of a row (xs >= 1). The mode is a type argument so the loop has no per-pixel dispatch.
    // The top-right neighbour of the last pixel in a row is the first pixel of the current row (RFC 9649 section 4.1).
    private static void Run<TP>(ReadOnlySpan<uint> src, Span<uint> dst, ReadOnlySpan<uint> prev, int xs, int xe)
        where TP : struct, IPredictor
    {
        int w = dst.Length;
        int inner = xe == w ? xe - 1 : xe;
        int n = inner - xs;
        uint l = dst[xs - 1];
        if (n > 0)
        {
            ReadOnlySpan<uint> s = src.Slice(xs, n);
            Span<uint> d = dst.Slice(xs, n);
            ReadOnlySpan<uint> pt = prev.Slice(xs, n);
            ReadOnlySpan<uint> ptl = prev.Slice(xs - 1, n);
            ReadOnlySpan<uint> ptr = prev.Slice(xs + 1, n);
            for (int i = 0; i < d.Length; i++)
            {
                l = AddPixels(s[i], TP.Predict(l, pt[i], ptl[i], ptr[i]));
                d[i] = l;
            }
        }

        if (inner < xe)
        {
            int x = w - 1;
            dst[x] = AddPixels(src[x], TP.Predict(l, prev[x], prev[x - 1], dst[0]));
        }
    }

    internal interface IPredictor
    {
        static abstract uint Predict(uint l, uint t, uint tl, uint tr);
    }

    private struct PBlack : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => 0xff000000; }

    private struct PLeft : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => l; }

    private struct PTop : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => t; }

    private struct PTopRight : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => tr; }

    private struct PTopLeft : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => tl; }

    private struct PAvg3 : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Average2(Average2(l, tr), t); }

    private struct PAvgLTl : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Average2(l, tl); }

    private struct PAvgLT : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Average2(l, t); }

    private struct PAvgTlT : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Average2(tl, t); }

    private struct PAvgTTr : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Average2(t, tr); }

    private struct PAvg4 : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Average2(Average2(l, tl), Average2(t, tr)); }

    private struct PSelect : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => Select(l, t, tl); }

    private struct PGradient : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => ClampAddSubtractFull(l, t, tl); }

    private struct PHalfGradient : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => ClampAddSubtractHalf(Average2(l, t), tl); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint AddPixels(uint a, uint b)
    {
        uint ag = (a & 0xff00ff00) + (b & 0xff00ff00);
        uint rb = (a & 0x00ff00ff) + (b & 0x00ff00ff);
        return (ag & 0xff00ff00) | (rb & 0x00ff00ff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefe) >> 1) + (a & b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AbsDiff(uint a, uint b, int shift)
    {
        int d = (int)((a >> shift) & 0xff) - (int)((b >> shift) & 0xff);
        int m = d >> 31;
        return (d ^ m) - m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ClampByte(int v)
    {
        // Branch-free clamp to 0..255 for v in -255..510: negative values become 0, values above 255 saturate.
        v &= ~(v >> 31);
        v |= (255 - v) >> 31;
        return (uint)v & 0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Select(uint l, uint t, uint tl)
    {
        // pL is the distance of the estimate (l + t - tl) to l, which is |t - tl| per channel; pT likewise.
        int pL = AbsDiff(t, tl, 0) + AbsDiff(t, tl, 8) + AbsDiff(t, tl, 16) + AbsDiff(t, tl, 24);
        int pT = AbsDiff(l, tl, 0) + AbsDiff(l, tl, 8) + AbsDiff(l, tl, 16) + AbsDiff(l, tl, 24);
        return pL < pT ? l : t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ClampAddSubtractFull(uint a, uint b, uint c)
    {
        return ClampByte((int)(a & 0xff) + (int)(b & 0xff) - (int)(c & 0xff))
            | (ClampByte((int)((a >> 8) & 0xff) + (int)((b >> 8) & 0xff) - (int)((c >> 8) & 0xff)) << 8)
            | (ClampByte((int)((a >> 16) & 0xff) + (int)((b >> 16) & 0xff) - (int)((c >> 16) & 0xff)) << 16)
            | (ClampByte((int)(a >> 24) + (int)(b >> 24) - (int)(c >> 24)) << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ClampAddSubtractHalf(uint a, uint b)
    {
        return HalfChannel(a, b, 0) | (HalfChannel(a, b, 8) << 8) | (HalfChannel(a, b, 16) << 16) | (HalfChannel(a, b, 24) << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HalfChannel(uint a, uint b, int shift)
    {
        int ca = (int)((a >> shift) & 0xff);
        int cb = (int)((b >> shift) & 0xff);
        // The spec divides by two truncating toward zero.
        int d = ca - cb;
        d = (d + (int)((uint)d >> 31)) >> 1;
        return ClampByte(ca + d);
    }

    internal static int DivRoundUp(int num, int den) => (num + den - 1) / den;

    private uint[] Rent(int count)
    {
        uint[] a = Pools<uint>.Pool.Rent(Math.Max(count, 1));
        _rented.Add(a);
        return a;
    }

    private sealed class Transform
    {
        public TransformKind Kind;
        public int XSize;
        public int YSize;
        public int Bits;
        public uint[]? Data;
    }
}
