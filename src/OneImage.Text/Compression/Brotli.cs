using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Text.Compression;

/// <summary>
/// Brotli decoder (RFC 7932), decode only, in safe managed code. The whole output is kept in one growing array, which
/// doubles as the sliding window; the array grows with the data actually produced, never to a size taken from the input,
/// and growth is refused past a caller-given byte cap and past an expansion ratio against the input consumed so far.
/// </summary>
internal static class Brotli
{
    /// <summary>Decompresses a complete Brotli stream.</summary>
    /// <param name="source">The compressed bytes. Bytes after the end of the stream are ignored.</param>
    /// <param name="maxOutput">The most bytes the stream may produce.</param>
    /// <param name="maxRatio">The most output bytes per input byte consumed (plus a 64 KiB allowance).</param>
    /// <param name="sizeHint">The expected output size, used only to size the first buffer (capped by the input size).</param>
    /// <param name="length">The number of bytes produced.</param>
    /// <returns>A buffer holding the output in its first <paramref name="length"/> bytes.</returns>
    /// <exception cref="FontFormatException">The stream is malformed, truncated or exceeds a limit.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> source, int maxOutput, int maxRatio, int sizeHint, out int length)
    {
        var d = new BrotliState(source, maxOutput, maxRatio, sizeHint);
        d.Run();
        length = d.OutLength;
        return d.Output;
    }
}

internal ref struct BrotliState
{
    private const int Link = 1 << 24;

    private static readonly byte[] DictOffsetBits = [0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5];
    private static readonly int[] DictOffset = BuildDictOffsets();
    private static byte[]? s_dictionary;

    private static ReadOnlySpan<int> BlockBase => [1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305, 369, 497, 753, 1265, 2289, 4337, 8433, 16625];

    private static ReadOnlySpan<byte> BlockExtra => [2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24];

    private static ReadOnlySpan<int> InsertBase => [0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594];

    private static ReadOnlySpan<byte> InsertExtra => [0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24];

    private static ReadOnlySpan<int> CopyBase => [2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118];

    private static ReadOnlySpan<byte> CopyExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24];

    // Insert-and-copy code / 64 -> first insert length code and first copy length code of the cell (RFC 7932 section 5).
    private static ReadOnlySpan<byte> CellInsert => [0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16];

    private static ReadOnlySpan<byte> CellCopy => [0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16];

    // Code-length-code prefix code (section 3.5), indexed by the next four bits: length and symbol.
    private static ReadOnlySpan<byte> ClLen => [2, 2, 2, 3, 2, 2, 2, 4, 2, 2, 2, 3, 2, 2, 2, 4];

    private static ReadOnlySpan<byte> ClSym => [0, 4, 3, 2, 0, 4, 3, 1, 0, 4, 3, 2, 0, 4, 3, 5];

    private static ReadOnlySpan<byte> ClOrder => [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly ReadOnlySpan<byte> _src;
    private readonly int _maxOutput;
    private readonly long _ratio;
    private ulong _buf;
    private int _bits;
    private int _pos;
    private byte[] _out;
    private int _outLen;
    private int[] _tab;
    private int _tabLen;
    private byte[] _lens;
    private ushort[] _sorted;
    private readonly int[] _last;

    public BrotliState(ReadOnlySpan<byte> src, int maxOutput, int ratio, int sizeHint)
    {
        _src = src;
        _maxOutput = maxOutput;
        _ratio = ratio;
        _buf = 0;
        _bits = 0;
        _pos = 0;
        _outLen = 0;
        int first = (int)Math.Clamp(Math.Min((long)sizeHint, (src.Length * 16L) + 4096), 16, Math.Max(16, maxOutput));
        _out = new byte[first];
        _tab = new int[4096];
        _tabLen = 0;
        _lens = new byte[704];
        _sorted = new ushort[704];
        _last = [4, 11, 15, 16];
    }

    public readonly byte[] Output => _out;

    public readonly int OutLength => _outLen;

    private static FontFormatException Bad(string what) => new("Brotli data is invalid: " + what + ".");

    private static int[] BuildDictOffsets()
    {
        var o = new int[26];
        for (int len = 0; len < 25; len++)
        {
            int words = len < 4 ? 0 : 1 << DictOffsetBits[len];
            o[len + 1] = o[len] + (len * words);
        }

        return o;
    }

    private static byte[] GetDictionary()
    {
        byte[]? d = s_dictionary;
        if (d is null)
        {
            using Stream s = typeof(Brotli).Assembly.GetManifestResourceStream("brotli-dictionary.bin")
                ?? throw new InvalidOperationException("The Brotli dictionary resource is missing.");
            d = new byte[DictOffset[25]];
            s.ReadExactly(d);
            s_dictionary = d;
        }

        return d;
    }

    // ---- bit reader ----------------------------------------------------------------------------------------------

    private void Refill()
    {
        if (_pos <= _src.Length - 8)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_src.Slice(_pos, 8));
            _buf |= v << _bits;
            int n = (63 - _bits) >> 3;
            _pos += n;
            _bits += n << 3;
            return;
        }

        while (_bits <= 56 && _pos < _src.Length)
        {
            _buf |= (ulong)_src[_pos++] << _bits;
            _bits += 8;
        }
    }

    private void Consume(int n)
    {
        _buf >>= n;
        _bits -= n;
        if (_bits < 0)
        {
            throw Bad("the stream is truncated");
        }
    }

    private uint ReadBits(int n)
    {
        if (_bits < n)
        {
            Refill();
        }

        uint v = (uint)(_buf & ((1UL << n) - 1));
        Consume(n);
        return v;
    }

    private int ReadSymbol(int tree)
    {
        if (_bits < 15)
        {
            Refill();
        }

        int e = _tab[tree + (int)(_buf & 0xFF)];
        if ((e & Link) != 0)
        {
            int subBits = (e >> 16) & 0xFF;
            e = _tab[tree + (e & 0xFFFF) + (int)((_buf >> 8) & ((1u << subBits) - 1))];
        }

        Consume((e >> 16) & 0xFF);
        return e & 0xFFFF;
    }

    // Drops the bits up to the next byte boundary, which must be zero, and gives back the whole bytes held in the buffer.
    private void AlignToByte()
    {
        int partial = _bits & 7;
        if ((_buf & ((1UL << partial) - 1)) != 0)
        {
            throw Bad("padding bits are not zero");
        }

        _pos -= _bits >> 3;
        _buf = 0;
        _bits = 0;
    }

    // ---- prefix codes --------------------------------------------------------------------------------------------

    private void EnsureTable(int needed)
    {
        if (needed > _tab.Length)
        {
            Array.Resize(ref _tab, Math.Max(needed, _tab.Length * 2));
        }
    }

    // Builds the lookup table for _lens[0..n) (a complete code, or a single symbol when single >= 0) and returns its base.
    private int BuildTable(int n, int single)
    {
        int tree = _tabLen;
        EnsureTable(tree + 256);
        if (single >= 0)
        {
            Array.Fill(_tab, single, tree, 256);
            _tabLen = tree + 256;
            return tree;
        }

        Span<int> count = stackalloc int[16];
        for (int i = 0; i < n; i++)
        {
            count[_lens[i]]++;
        }

        count[0] = 0;
        Span<int> offs = stackalloc int[17];
        for (int l = 1; l < 16; l++)
        {
            offs[l + 1] = offs[l] + count[l];
        }

        for (int i = 0; i < n; i++)
        {
            int l = _lens[i];
            if (l != 0)
            {
                _sorted[offs[l]++] = (ushort)i;
            }
        }

        // Pass 1: how long a second-level table each 8-bit prefix needs.
        Span<byte> subBits = stackalloc byte[256];
        subBits.Clear();
        int idx = 0;
        int code = 0;
        for (int l = 1; l <= 15; l++)
        {
            for (int c = 0; c < count[l]; c++, idx++, code++)
            {
                if (l > 8)
                {
                    int rev = Reverse(code, l);
                    int p = rev & 0xFF;
                    if (l - 8 > subBits[p])
                    {
                        subBits[p] = (byte)(l - 8);
                    }
                }
            }

            code <<= 1;
        }

        int next = 256;
        Span<int> subOff = stackalloc int[256];
        for (int p = 0; p < 256; p++)
        {
            if (subBits[p] != 0)
            {
                subOff[p] = next;
                next += 1 << subBits[p];
            }
        }

        EnsureTable(tree + next);
        for (int p = 0; p < 256; p++)
        {
            if (subBits[p] != 0)
            {
                _tab[tree + p] = Link | (subBits[p] << 16) | subOff[p];
            }
        }

        // Pass 2: fill the entries.
        idx = 0;
        code = 0;
        for (int l = 1; l <= 15; l++)
        {
            for (int c = 0; c < count[l]; c++, idx++, code++)
            {
                int sym = _sorted[idx];
                int rev = Reverse(code, l);
                int entry = (l << 16) | sym;
                if (l <= 8)
                {
                    for (int k = rev; k < 256; k += 1 << l)
                    {
                        _tab[tree + k] = entry;
                    }
                }
                else
                {
                    int p = rev & 0xFF;
                    int size = 1 << subBits[p];
                    for (int k = rev >> 8; k < size; k += 1 << (l - 8))
                    {
                        _tab[tree + subOff[p] + k] = entry;
                    }
                }
            }

            code <<= 1;
        }

        _tabLen = tree + next;
        return tree;
    }

    private static int Reverse(int code, int len)
    {
        int r = 0;
        for (int i = 0; i < len; i++)
        {
            r = (r << 1) | (code & 1);
            code >>= 1;
        }

        return r;
    }

    // Reads one prefix code over an alphabet of the given size (RFC 7932 section 3.4 and 3.5); returns its table base.
    private int ReadPrefixCode(int alphabet)
    {
        if (alphabet > _lens.Length)
        {
            throw Bad("alphabet too large");
        }

        uint kind = ReadBits(2);
        if (kind == 1)
        {
            int symBits = 32 - BitOperations.LeadingZeroCount((uint)(alphabet - 1));
            int nsym = (int)ReadBits(2) + 1;
            Span<int> syms = stackalloc int[4];
            for (int i = 0; i < nsym; i++)
            {
                syms[i] = (int)ReadBits(symBits);
                if (syms[i] >= alphabet)
                {
                    throw Bad("symbol outside the alphabet");
                }

                for (int j = 0; j < i; j++)
                {
                    if (syms[j] == syms[i])
                    {
                        throw Bad("duplicate symbol");
                    }
                }
            }

            Array.Clear(_lens, 0, alphabet);
            switch (nsym)
            {
                case 1:
                    return BuildTable(alphabet, syms[0]);
                case 2:
                    _lens[syms[0]] = 1;
                    _lens[syms[1]] = 1;
                    break;
                case 3:
                    _lens[syms[0]] = 1;
                    _lens[syms[1]] = 2;
                    _lens[syms[2]] = 2;
                    break;
                default:
                    if (ReadBits(1) == 0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            _lens[syms[i]] = 2;
                        }
                    }
                    else
                    {
                        _lens[syms[0]] = 1;
                        _lens[syms[1]] = 2;
                        _lens[syms[2]] = 3;
                        _lens[syms[3]] = 3;
                    }

                    break;
            }

            return BuildTable(alphabet, -1);
        }

        // Complex code: first the code lengths of the code-length alphabet.
        Array.Clear(_lens, 0, 18);
        int space = 32;
        int numCodes = 0;
        for (int i = (int)kind; i < 18; i++)
        {
            if (_bits < 4)
            {
                Refill();
            }

            int v = (int)(_buf & 15);
            Consume(ClLen[v]);
            int len = ClSym[v];
            _lens[ClOrder[i]] = (byte)len;
            if (len != 0)
            {
                space -= 32 >> len;
                numCodes++;
                if (space <= 0)
                {
                    break;
                }
            }
        }

        if (numCodes != 1 && space != 0)
        {
            throw Bad("incomplete code-length code");
        }

        // The code-length code needs its own table; build it in the scratch area, then the real one afterwards.
        int clTree = BuildCodeLengthTable(numCodes);

        Array.Clear(_lens, 0, alphabet);
        int symbol = 0;
        int prevLen = 8;
        int repeat = 0;
        int repeatLen = 0;
        int remaining = 32768;
        while (symbol < alphabet && remaining > 0)
        {
            int s = ReadSymbol(clTree);
            if (s < 16)
            {
                repeat = 0;
                if (s != 0)
                {
                    _lens[symbol] = (byte)s;
                    prevLen = s;
                    remaining -= 32768 >> s;
                }

                symbol++;
                continue;
            }

            int extra = s == 16 ? 2 : 3;
            int newLen = s == 16 ? prevLen : 0;
            if (repeatLen != newLen)
            {
                repeat = 0;
                repeatLen = newLen;
            }

            int old = repeat;
            if (repeat > 0)
            {
                repeat = (repeat - 2) << extra;
            }

            repeat += (int)ReadBits(extra) + 3;
            int delta = repeat - old;
            if (symbol + delta > alphabet)
            {
                throw Bad("code-length repeat overruns the alphabet");
            }

            for (int k = 0; k < delta; k++)
            {
                _lens[symbol + k] = (byte)repeatLen;
            }

            symbol += delta;
            if (repeatLen != 0)
            {
                remaining -= delta << (15 - repeatLen);
            }
        }

        if (remaining != 0)
        {
            throw Bad("incomplete prefix code");
        }

        // Drop the code-length table: the code just read goes where it started.
        _tabLen = clTree;
        return BuildTable(alphabet, -1);
    }

    private int BuildCodeLengthTable(int numCodes)
    {
        // The code-length code lengths sit in _lens[0..18) and the generic builder reads them from there.
        if (numCodes == 1)
        {
            int only = 0;
            for (int i = 0; i < 18; i++)
            {
                if (_lens[i] != 0)
                {
                    only = i;
                }
            }

            return BuildTable(18, only);
        }

        return BuildTable(18, -1);
    }

    private int ReadVarLen()
    {
        // 1..256, the variable-length count used for block types and trees (section 9.2).
        if (ReadBits(1) == 0)
        {
            return 1;
        }

        int n = (int)ReadBits(3);
        if (n == 0)
        {
            return 2;
        }

        return (int)ReadBits(n) + (1 << n) + 1;
    }

    private byte[] ReadContextMap(int size, int numTrees)
    {
        var map = new byte[size];
        int rleMax = 0;
        if (ReadBits(1) != 0)
        {
            rleMax = (int)ReadBits(4) + 1;
        }

        int tree = ReadPrefixCode(numTrees + rleMax);
        int i = 0;
        while (i < size)
        {
            int s = ReadSymbol(tree);
            if (s == 0)
            {
                i++;
            }
            else if (s <= rleMax)
            {
                int reps = (1 << s) + (int)ReadBits(s);
                if (i + reps > size)
                {
                    throw Bad("context map run overruns the map");
                }

                i += reps;
            }
            else
            {
                map[i++] = (byte)(s - rleMax);
            }
        }

        if (ReadBits(1) != 0)
        {
            Span<byte> mtf = stackalloc byte[256];
            for (int k = 0; k < 256; k++)
            {
                mtf[k] = (byte)k;
            }

            for (int k = 0; k < size; k++)
            {
                int index = map[k];
                byte value = mtf[index];
                map[k] = value;
                for (; index > 0; index--)
                {
                    mtf[index] = mtf[index - 1];
                }

                mtf[0] = value;
            }
        }

        return map;
    }

    // ---- output --------------------------------------------------------------------------------------------------

    private void Reserve(int n)
    {
        long need = (long)_outLen + n;
        if (need <= _out.Length)
        {
            return;
        }

        if (need > _maxOutput)
        {
            throw Bad("the output is larger than allowed");
        }

        if (need > (((long)_pos + 16) * _ratio) + 65536)
        {
            throw Bad("the data expands more than allowed");
        }

        long grown = Math.Min(Math.Max(2L * _out.Length, need), _maxOutput);
        Array.Resize(ref _out, (int)grown);
    }

    // ---- stream --------------------------------------------------------------------------------------------------

    public void Run()
    {
        // Stream header: window size (section 9.1).
        int wbits;
        if (ReadBits(1) == 0)
        {
            wbits = 16;
        }
        else
        {
            int n = (int)ReadBits(3);
            if (n != 0)
            {
                wbits = 17 + n;
            }
            else
            {
                int m = (int)ReadBits(3);
                if (m == 1)
                {
                    throw Bad("invalid window size");
                }

                wbits = m == 0 ? 17 : 8 + m;
            }
        }

        int window = (1 << wbits) - 16;
        bool last;
        do
        {
            last = ReadBits(1) != 0;
            if (last && ReadBits(1) != 0)
            {
                break;
            }

            int nibbles = (int)ReadBits(2);
            if (nibbles == 3)
            {
                SkipMetadata();
                continue;
            }

            nibbles += 4;
            int mlen = 0;
            for (int i = 0; i < nibbles; i++)
            {
                uint nib = ReadBits(4);
                if (i == nibbles - 1 && nibbles > 4 && nib == 0)
                {
                    throw Bad("meta-block length has a leading zero nibble");
                }

                mlen |= (int)(nib << (4 * i));
            }

            mlen++;
            if (!last && ReadBits(1) != 0)
            {
                CopyUncompressed(mlen);
                continue;
            }

            CompressedMetaBlock(mlen, window);
        }
        while (!last);

        // Bits after the end of the last meta-block, up to the byte boundary, must be zero.
        if ((_buf & ((1UL << (_bits & 7)) - 1)) != 0)
        {
            throw Bad("padding bits are not zero");
        }
    }

    private void SkipMetadata()
    {
        if (ReadBits(1) != 0)
        {
            throw Bad("reserved bit set");
        }

        int skipBytes = (int)ReadBits(2);
        int skip = 0;
        for (int i = 0; i < skipBytes; i++)
        {
            uint b = ReadBits(8);
            if (i == skipBytes - 1 && skipBytes > 1 && b == 0)
            {
                throw Bad("metadata length has a zero last byte");
            }

            skip |= (int)(b << (8 * i));
        }

        if (skipBytes > 0)
        {
            skip++;
        }

        AlignToByte();
        if (skip > _src.Length - _pos)
        {
            throw Bad("the stream is truncated");
        }

        _pos += skip;
    }

    private void CopyUncompressed(int mlen)
    {
        if (mlen > _maxOutput - _outLen)
        {
            throw Bad("the output is larger than allowed");
        }

        AlignToByte();
        if (mlen > _src.Length - _pos)
        {
            throw Bad("the stream is truncated");
        }

        _pos += mlen;
        Reserve(mlen);
        _src.Slice(_pos - mlen, mlen).CopyTo(_out.AsSpan(_outLen));
        _outLen += mlen;
    }

    private struct BlockState
    {
        public int Types;
        public int Type;
        public int Prev;
        public int Remaining;
        public int TypeTree;
        public int CountTree;
    }

    private BlockState ReadBlockHeader(int types)
    {
        var b = new BlockState { Types = types, Type = 0, Prev = 1, Remaining = 1 << 24 };
        if (types >= 2)
        {
            b.TypeTree = ReadPrefixCode(types + 2);
            b.CountTree = ReadPrefixCode(26);
            b.Remaining = ReadBlockCount(b.CountTree);
        }

        return b;
    }

    private int ReadBlockCount(int tree)
    {
        int code = ReadSymbol(tree);
        return BlockBase[code] + (int)ReadBits(BlockExtra[code]);
    }

    private void SwitchBlock(ref BlockState b)
    {
        int code = ReadSymbol(b.TypeTree);
        int type = code switch
        {
            0 => b.Prev,
            1 => b.Type + 1,
            _ => code - 2,
        };

        if (type >= b.Types)
        {
            type -= b.Types;
        }

        b.Prev = b.Type;
        b.Type = type;
        b.Remaining = ReadBlockCount(b.CountTree);
    }

    private void CompressedMetaBlock(int mlen, int window)
    {
        if (mlen > _maxOutput - _outLen)
        {
            throw Bad("the output is larger than allowed");
        }

        _tabLen = 0;
        BlockState lit = ReadBlockHeader(ReadVarLen());
        BlockState iac = ReadBlockHeader(ReadVarLen());
        BlockState dist = ReadBlockHeader(ReadVarLen());
        int postfix = (int)ReadBits(2);
        int direct = (int)ReadBits(4) << postfix;

        var modes = new byte[lit.Types];
        for (int i = 0; i < modes.Length; i++)
        {
            modes[i] = (byte)ReadBits(2);
        }

        int litTreeCount = ReadVarLen();
        byte[] litMap = litTreeCount >= 2 ? ReadContextMap(64 * lit.Types, litTreeCount) : new byte[64 * lit.Types];
        int distTreeCount = ReadVarLen();
        byte[] distMap = distTreeCount >= 2 ? ReadContextMap(4 * dist.Types, distTreeCount) : new byte[4 * dist.Types];

        var litTrees = new int[litTreeCount];
        for (int i = 0; i < litTrees.Length; i++)
        {
            litTrees[i] = ReadPrefixCode(256);
        }

        var iacTrees = new int[iac.Types];
        for (int i = 0; i < iacTrees.Length; i++)
        {
            iacTrees[i] = ReadPrefixCode(704);
        }

        int distAlphabet = 16 + direct + (48 << postfix);
        var distTrees = new int[distTreeCount];
        for (int i = 0; i < distTrees.Length; i++)
        {
            distTrees[i] = ReadPrefixCode(distAlphabet);
        }

        int metaEnd = _outLen + mlen;
        ReadOnlySpan<byte> lut0 = BrotliTables.Lut0;
        ReadOnlySpan<byte> lut1 = BrotliTables.Lut1;
        ReadOnlySpan<byte> lut2 = BrotliTables.Lut2;
        int litBase = 0;
        int litMode = modes[0];

        while (_outLen < metaEnd)
        {
            if (iac.Remaining == 0)
            {
                SwitchBlock(ref iac);
            }

            iac.Remaining--;
            int code = ReadSymbol(iacTrees[iac.Type]);
            int cell = code >> 6;
            int insertCode = CellInsert[cell] + ((code >> 3) & 7);
            int copyCode = CellCopy[cell] + (code & 7);
            int insertLen = InsertBase[insertCode] + (int)ReadBits(InsertExtra[insertCode]);
            int copyLen = CopyBase[copyCode] + (int)ReadBits(CopyExtra[copyCode]);

            if (insertLen > metaEnd - _outLen)
            {
                throw Bad("literals run past the meta-block");
            }

            for (int i = 0; i < insertLen; i++)
            {
                if (_outLen >= _out.Length)
                {
                    Reserve(1);
                }

                if (lit.Remaining == 0)
                {
                    SwitchBlock(ref lit);
                    litBase = 64 * lit.Type;
                    litMode = modes[lit.Type];
                }

                lit.Remaining--;
                int p1 = _outLen > 0 ? _out[_outLen - 1] : 0;
                int p2 = _outLen > 1 ? _out[_outLen - 2] : 0;
                int ctx = litMode switch
                {
                    0 => p1 & 0x3F,
                    1 => p1 >> 2,
                    2 => lut0[p1] | lut1[p2],
                    _ => (lut2[p1] << 3) | lut2[p2],
                };

                _out[_outLen++] = (byte)ReadSymbol(litTrees[litMap[litBase + ctx]]);
            }

            if (_outLen == metaEnd)
            {
                break;
            }

            long distance;
            bool push = true;
            if (code < 128)
            {
                distance = _last[0];
                push = false;
            }
            else
            {
                if (dist.Remaining == 0)
                {
                    SwitchBlock(ref dist);
                }

                dist.Remaining--;
                int dctx = copyLen > 4 ? 3 : copyLen - 2;
                int sym = ReadSymbol(distTrees[distMap[(4 * dist.Type) + dctx]]);
                if (sym < 16)
                {
                    if (sym == 0)
                    {
                        push = false;
                    }

                    distance = ShortDistance(sym);
                    if (distance <= 0)
                    {
                        throw Bad("distance is not positive");
                    }
                }
                else if (sym < 16 + direct)
                {
                    distance = sym - 15;
                }
                else
                {
                    int d = sym - direct - 16;
                    int nbits = 1 + (d >> (postfix + 1));
                    long hcode = d >> postfix;
                    long lcode = d & ((1 << postfix) - 1);
                    long offset = ((2 + (hcode & 1)) << nbits) - 4;
                    long extra = ReadBits(nbits);
                    distance = (((offset + extra) << postfix) + lcode) + direct + 1;
                }
            }

            int maxDistance = Math.Min(window, _outLen);
            if (copyLen > metaEnd - _outLen)
            {
                if (distance <= maxDistance)
                {
                    throw Bad("copy runs past the meta-block");
                }
            }

            if (distance <= maxDistance)
            {
                if (push)
                {
                    PushDistance((int)distance);
                }

                Reserve(copyLen);
                int from = _outLen - (int)distance;
                if (distance >= copyLen)
                {
                    _out.AsSpan(from, copyLen).CopyTo(_out.AsSpan(_outLen));
                    _outLen += copyLen;
                }
                else
                {
                    for (int i = 0; i < copyLen; i++)
                    {
                        _out[_outLen++] = _out[from++];
                    }
                }
            }
            else
            {
                // A static dictionary reference (section 8); it is not pushed on the distance ring.
                if (copyLen < 4 || copyLen > 24)
                {
                    throw Bad("dictionary word length out of range");
                }

                long wordId = distance - (maxDistance + 1);
                int bitsForLen = DictOffsetBits[copyLen];
                int index = (int)(wordId & ((1 << bitsForLen) - 1));
                long transform = wordId >> bitsForLen;
                if (transform > 120)
                {
                    throw Bad("dictionary transform out of range");
                }

                AppendDictionaryWord(copyLen, index, (int)transform, metaEnd - _outLen);
            }
        }
    }

    private readonly long ShortDistance(int sym)
    {
        if (sym < 4)
        {
            return _last[sym];
        }

        int s = sym - 4;
        int baseIdx = s >= 6 ? 1 : 0;
        s %= 6;
        int delta = (s >> 1) + 1;
        return _last[baseIdx] + ((s & 1) == 0 ? -delta : delta);
    }

    private void PushDistance(int d)
    {
        _last[3] = _last[2];
        _last[2] = _last[1];
        _last[1] = _last[0];
        _last[0] = d;
    }

    private void AppendDictionaryWord(int len, int index, int transformId, int remaining)
    {
        (byte[] prefix, byte kind, byte[] suffix) = BrotliTables.Transforms[transformId];
        byte[] dict = GetDictionary();
        int off = DictOffset[len] + (index * len);
        int start = off;
        int wordLen = len;
        if (kind >= 3 && kind <= 11)
        {
            int skip = kind - 2;
            if (skip >= len)
            {
                wordLen = 0;
            }
            else
            {
                start += skip;
                wordLen = len - skip;
            }
        }
        else if (kind >= 12)
        {
            int cut = kind - 11;
            wordLen = cut >= len ? 0 : len - cut;
        }

        int total = prefix.Length + wordLen + suffix.Length;
        if (total > remaining)
        {
            throw Bad("dictionary word runs past the meta-block");
        }

        Reserve(total);
        prefix.CopyTo(_out.AsSpan(_outLen));
        int wordAt = _outLen + prefix.Length;
        dict.AsSpan(start, wordLen).CopyTo(_out.AsSpan(wordAt));
        suffix.CopyTo(_out.AsSpan(wordAt + wordLen));
        if (kind == 1)
        {
            if (wordLen > 0)
            {
                Ferment(_out.AsSpan(wordAt, wordLen), 0);
            }
        }
        else if (kind == 2)
        {
            Span<byte> w = _out.AsSpan(wordAt, wordLen);
            int i = 0;
            while (i < w.Length)
            {
                i += Ferment(w, i);
            }
        }

        _outLen += total;
    }

    // RFC 7932 section 8: upper-case the character at pos; returns the width of the UTF-8 sequence handled.
    private static int Ferment(Span<byte> word, int pos)
    {
        byte c = word[pos];
        if (c < 192)
        {
            if (c >= 97 && c <= 122)
            {
                word[pos] = (byte)(c ^ 32);
            }

            return 1;
        }

        if (c < 224)
        {
            if (pos + 1 < word.Length)
            {
                word[pos + 1] ^= 32;
            }

            return 2;
        }

        if (pos + 2 < word.Length)
        {
            word[pos + 2] ^= 5;
        }

        return 3;
    }
}
