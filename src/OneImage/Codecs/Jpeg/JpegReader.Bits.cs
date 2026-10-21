using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OneImage.Codecs.Jpeg;

internal sealed partial class JpegReader
{
    // Entropy-coded segment state. The bit buffer is MSB-aligned: the next bit is bit 63.
    private ulong _bits;
    private int _count;

    // Number of zero bits appended after the data ended (marker or end of file). Consuming them means the scan was cut short.
    private int _pad;

    // A marker the entropy reader ran into (already consumed), -1 at the end of the data, 0 for none.
    private int _marker;

    /// <summary>Gets whether decoding consumed bits that were not in the file.</summary>
    public bool Overrun
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count < _pad;
    }

    /// <summary>Starts a scan or restart interval: drops buffered bits.</summary>
    public void ResetBits()
    {
        _bits = 0;
        _count = 0;
        _pad = 0;
        _arithPad = 0;
    }

    private int _arithPad;

    /// <summary>
    /// Returns the next byte of entropy-coded data with byte stuffing removed (for the arithmetic decoder), or
    /// zero once a marker or the end of the data is reached.
    /// </summary>
    /// <exception cref="ImageFormatException">Far more bytes were requested past the end than a complete scan can need.</exception>
    public int NextEntropyByte()
    {
        if (_marker == 0)
        {
            int b = TryReadByte();
            if (b < 0)
            {
                _marker = -1;
            }
            else if (b != 0xFF)
            {
                return b;
            }
            else
            {
                int c;
                do
                {
                    c = TryReadByte();
                }
                while (c == 0xFF);
                if (c == 0)
                {
                    return 0xFF;
                }

                _marker = c < 0 ? -1 : c;
            }
        }

        if (++_arithPad > 16)
        {
            throw new ImageFormatException("JPEG scan data ended early.");
        }

        return 0;
    }

    /// <summary>Clears the pending marker (used before reading headers of a new scan).</summary>
    public void ClearPendingMarker() => _marker = 0;

    /// <summary>
    /// Handles a restart boundary: discards the partial byte, then consumes the RSTn marker.
    /// </summary>
    /// <exception cref="ImageFormatException">The next marker is not a restart marker.</exception>
    public void ProcessRestart()
    {
        ResetBits();
        int m = NextMarker();
        if (m is < 0xD0 or > 0xD7)
        {
            throw new ImageFormatException("Expected a JPEG restart marker.");
        }
    }

    /// <summary>Appends bits so that at least 32 are available.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Fill()
    {
        // Once a marker has been met only zero padding follows; the fast path must not read past it.
        if (_marker == 0)
        {
            if (_end - _pos < 8 && !_eof)
            {
                Refill();
            }

            if (_end - _pos >= 8)
            {
                ulong v = BinaryPrimitives.ReadUInt64BigEndian(_buf.AsSpan(_pos, 8));
                if (!HasFF(v))
                {
                    _bits |= v >> _count;
                    int n = (64 - _count) >> 3;
                    _pos += n;
                    _count += n << 3;
                    return;
                }
            }
        }

        while (_count <= 56)
        {
            if (_marker != 0)
            {
                _count += 8;
                _pad += 8;
                continue;
            }

            int b = TryReadByte();
            if (b < 0)
            {
                _marker = -1;
                continue;
            }

            if (b == 0xFF)
            {
                int c;
                do
                {
                    c = TryReadByte();
                }
                while (c == 0xFF);
                if (c < 0)
                {
                    _marker = -1;
                    continue;
                }

                if (c != 0)
                {
                    _marker = c;
                    continue;
                }
            }

            _bits |= (ulong)b << (56 - _count);
            _count += 8;
        }
    }

    /// <summary>Decodes one Huffman symbol. At least 16 bits must be buffered (call <see cref="Fill"/> when fewer than 32).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeHuffman(JpegHuffmanTable t)
    {
        int e = t.Fast[(int)(_bits >> (64 - JpegHuffmanTable.FastBits))];
        if (e != 0)
        {
            int l = e >> 8;
            _bits <<= l;
            _count -= l;
            return e & 0xFF;
        }

        return DecodeHuffmanSlow(t);
    }

    private int DecodeHuffmanSlow(JpegHuffmanTable t)
    {
        int code = (int)(_bits >> 48);
        for (int l = JpegHuffmanTable.FastBits + 1; l <= 16; l++)
        {
            int c = code >> (16 - l);
            if (c <= t.MaxCode[l])
            {
                int idx = t.ValPtr[l] + c - t.MinCode[l];
                if ((uint)idx >= (uint)t.Count)
                {
                    break;
                }

                _bits <<= l;
                _count -= l;
                return t.Values[idx];
            }
        }

        throw new ImageFormatException("Invalid JPEG Huffman code.");
    }

    /// <summary>
    /// Decodes one sequential block (T.81 F.2.2): the DC difference and the run-length coded AC coefficients, written in
    /// natural order. The hot loop keeps the bit buffer in locals, which is what makes the entropy stage fast.
    /// </summary>
    /// <param name="block">64 coefficients; cleared here.</param>
    /// <param name="pred">The component's running DC value.</param>
    /// <param name="dc">DC table.</param>
    /// <param name="ac">AC table.</param>
    /// <param name="dcOnly">Parse the AC data but do not store it.</param>
    public void DecodeBlockBaseline(Span<short> block, ref int pred, JpegHuffmanTable dc, JpegHuffmanTable ac, bool dcOnly)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("A block needs 64 coefficients.", nameof(block));
        }

        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        ushort[] dcFast = dc.Fast;
        ushort[] acFast = ac.Fast;
        block[..64].Clear();
        ulong bits = _bits;
        int count = _count;
        if (count < 32)
        {
            _bits = bits;
            _count = count;
            Fill();
            bits = _bits;
            count = _count;
        }

        int e = dcFast[(int)(bits >> (64 - JpegHuffmanTable.FastBits))];
        int t;
        if (e != 0)
        {
            int l = e >> 8;
            bits <<= l;
            count -= l;
            t = e & 0xFF;
        }
        else
        {
            _bits = bits;
            _count = count;
            t = DecodeHuffmanSlow(dc);
            bits = _bits;
            count = _count;
        }

        if (t > 15)
        {
            throw new ImageFormatException("Invalid JPEG DC coefficient size.");
        }

        if (t != 0)
        {
            int v = (int)(bits >> (64 - t));
            bits <<= t;
            count -= t;
            pred += v + (((v - (1 << (t - 1))) >> 31) & ((-1 << t) + 1));
        }

        block[0] = (short)pred;
        for (int k = 1; k < 64;)
        {
            if (count < 32)
            {
                _bits = bits;
                _count = count;
                Fill();
                bits = _bits;
                count = _count;
            }

            e = acFast[(int)(bits >> (64 - JpegHuffmanTable.FastBits))];
            int rs;
            if (e != 0)
            {
                int l = e >> 8;
                bits <<= l;
                count -= l;
                rs = e & 0xFF;
            }
            else
            {
                _bits = bits;
                _count = count;
                rs = DecodeHuffmanSlow(ac);
                bits = _bits;
                count = _count;
            }

            int s = rs & 15;
            if (s == 0)
            {
                if (rs != 0xF0)
                {
                    break;
                }

                k += 16;
                continue;
            }

            k += rs >> 4;
            if (k > 63)
            {
                throw new ImageFormatException("JPEG AC coefficient index out of range.");
            }

            int val = (int)(bits >> (64 - s));
            bits <<= s;
            count -= s;
            if (!dcOnly)
            {
                block[zz[k] & 63] = (short)(val + (((val - (1 << (s - 1))) >> 31) & ((-1 << s) + 1)));
            }

            k++;
        }

        _bits = bits;
        _count = count;
    }

    /// <summary>Reads <paramref name="n"/> (1..16) bits as an unsigned value. Needs the buffer filled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBits(int n)
    {
        int v = (int)(_bits >> (64 - n));
        _bits <<= n;
        _count -= n;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit()
    {
        int v = (int)(_bits >> 63);
        _bits <<= 1;
        _count--;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int n)
    {
        _bits <<= n;
        _count -= n;
    }

    /// <summary>Gets the buffered bit count.</summary>
    public int BitCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>Reads an <paramref name="s"/>-bit magnitude and sign-extends it (T.81 F.2.2.1 EXTEND).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReceiveExtend(int s)
    {
        int v = GetBits(s);
        return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
    }
}
