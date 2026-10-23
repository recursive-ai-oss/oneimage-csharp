using System.Buffers.Binary;

namespace OneImage.Codecs.Jpeg;

// Progressive AC scans (T.81 G.1.2.2 and G.1.2.3). Like the sequential block decoder these keep the bit buffer in locals, refill
// it once per symbol without branching on the bit count, and resolve most symbols with one lookup in the 12-bit table.
internal sealed partial class JpegReader
{
    /// <summary>
    /// Decodes the AC coefficients of one block in a first progressive AC scan (spectral band <paramref name="ss"/>..<paramref name="se"/>,
    /// point transform <paramref name="al"/>), or consumes one block of a pending end-of-band run.
    /// </summary>
    /// <param name="block">64 coefficients, natural order.</param>
    /// <param name="ac">AC table.</param>
    /// <param name="ss">First coefficient (zigzag index) of the band.</param>
    /// <param name="se">Last coefficient (zigzag index) of the band.</param>
    /// <param name="al">Point transform (bit position) of the scan.</param>
    /// <param name="eobRun">Number of further blocks still covered by an end-of-band run.</param>
    public void DecodeAcFirst(Span<short> block, JpegHuffmanTable ac, int ss, int se, int al, ref int eobRun)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        int[] look = ac.LookOrNone;
        ushort[] acFast = ac.Fast;
        block = block[..64];
        ulong bits = _bits;
        int count = _count;
        byte[] buf = _buf;
        int pos = _pos;
        int fastEnd = _marker == 0 ? _end - 8 : -1;
        ulong w;
        for (int k = ss; k <= se; k++)
        {
            if (pos <= fastEnd && !HasFF(w = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos, 8))))
            {
                bits |= w >> count;
                pos += (63 - count) >> 3;
                count |= 56;
            }
            else if (count < 32)
            {
                _bits = bits;
                _count = count;
                _pos = pos;
                Fill();
                bits = _bits;
                count = _count;
                pos = _pos;
                fastEnd = _marker == 0 ? _end - 8 : -1;
            }

            int e = look[(int)(bits >> (64 - JpegHuffmanTable.LookBits))];
            int run;
            if (e != 0)
            {
                int used = e & 31;
                bits <<= used;
                count -= used;
                run = (e >> 8) & 15;
                if ((e & 0x20) == 0)
                {
                    k += run;
                    if (k > 63)
                    {
                        throw new ImageFormatException("JPEG AC coefficient index out of range.");
                    }

                    block[zz[k] & 63] = (short)((e >> 16) << al);
                    continue;
                }
            }
            else
            {
                int rs;
                e = acFast[(int)(bits >> (64 - JpegHuffmanTable.FastBits))];
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
                run = rs >> 4;
                if (s != 0)
                {
                    k += run;
                    if (k > 63)
                    {
                        throw new ImageFormatException("JPEG AC coefficient index out of range.");
                    }

                    int val = (int)(bits >> (64 - s));
                    bits <<= s;
                    count -= s;
                    val += ((val - (1 << (s - 1))) >> 31) & ((-1 << s) + 1);
                    block[zz[k] & 63] = (short)(val << al);
                    continue;
                }
            }

            // No coefficient: a run of sixteen zeros, or an end-of-band run of 2^run + extra blocks (this one included).
            if (run == 15)
            {
                k += 15;
                continue;
            }

            int eob = 1 << run;
            if (run != 0)
            {
                eob += (int)(bits >> (64 - run));
                bits <<= run;
                count -= run;
            }

            eobRun = eob - 1;
            break;
        }

        _bits = bits;
        _count = count;
        _pos = pos;
    }

    /// <summary>
    /// Decodes one block of a refinement progressive AC scan (T.81 G.1.2.3): correction bits for coefficients that are already
    /// nonzero, and newly nonzero coefficients of magnitude one at the current bit position.
    /// </summary>
    /// <param name="block">64 coefficients, natural order.</param>
    /// <param name="ac">AC table.</param>
    /// <param name="ss">First coefficient (zigzag index) of the band.</param>
    /// <param name="se">Last coefficient (zigzag index) of the band.</param>
    /// <param name="al">Point transform (bit position) of the scan.</param>
    /// <param name="eobRun">Number of further blocks still covered by an end-of-band run.</param>
    public void DecodeAcRefine(Span<short> block, JpegHuffmanTable ac, int ss, int se, int al, ref int eobRun)
    {
        ReadOnlySpan<byte> zz = JpegConstants.Zigzag;
        int[] look = ac.LookOrNone;
        ushort[] acFast = ac.Fast;
        block = block[..64];
        int p1 = 1 << al;
        int m1 = -1 << al;
        ulong bits = _bits;
        int count = _count;
        byte[] buf = _buf;
        int pos = _pos;
        int fastEnd = _marker == 0 ? _end - 8 : -1;
        ulong w;
        int k = ss;
        if (eobRun == 0)
        {
            for (; k <= se; k++)
            {
                if (pos <= fastEnd && !HasFF(w = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos, 8))))
                {
                    bits |= w >> count;
                    pos += (63 - count) >> 3;
                    count |= 56;
                }
                else if (count < 32)
                {
                    _bits = bits;
                    _count = count;
                    _pos = pos;
                    Fill();
                    bits = _bits;
                    count = _count;
                    pos = _pos;
                    fastEnd = _marker == 0 ? _end - 8 : -1;
                }

                int e = look[(int)(bits >> (64 - JpegHuffmanTable.LookBits))];
                int s;
                int run;
                if (e != 0)
                {
                    int used = e & 31;
                    bits <<= used;
                    count -= used;
                    run = (e >> 8) & 15;
                    if ((e & 0x20) != 0)
                    {
                        s = 0;
                    }
                    else
                    {
                        // The magnitude must be one (size 1); its sign bit is the lowest bit consumed.
                        s = e >> 16;
                        if (s is not (1 or -1))
                        {
                            throw new ImageFormatException("Invalid JPEG progressive refinement.");
                        }
                    }
                }
                else
                {
                    int rs;
                    e = acFast[(int)(bits >> (64 - JpegHuffmanTable.FastBits))];
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

                    run = rs >> 4;
                    s = rs & 15;
                    if (s != 0)
                    {
                        if (s != 1)
                        {
                            throw new ImageFormatException("Invalid JPEG progressive refinement.");
                        }

                        s = (int)(bits >> 63) != 0 ? 1 : -1;
                        bits <<= 1;
                        count--;
                    }
                }

                if (s == 0)
                {
                    if (run != 15)
                    {
                        int eob = 1 << run;
                        if (run != 0)
                        {
                            eob += (int)(bits >> (64 - run));
                            bits <<= run;
                            count -= run;
                        }

                        eobRun = eob;
                        break;
                    }
                }
                else
                {
                    s = s > 0 ? p1 : m1;
                }

                // Skip over already-nonzero coefficients (refining them) and 'run' zero ones.
                do
                {
                    ref short coef = ref block[zz[k] & 63];
                    if (coef != 0)
                    {
                        if (count < 1)
                        {
                            _bits = bits;
                            _count = count;
                            _pos = pos;
                            Fill();
                            bits = _bits;
                            count = _count;
                            pos = _pos;
                            fastEnd = _marker == 0 ? _end - 8 : -1;
                        }

                        int bit = (int)(bits >> 63);
                        bits <<= 1;
                        count--;
                        if (bit != 0 && (coef & p1) == 0)
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

                    block[zz[k] & 63] = (short)s;
                }
            }
        }

        if (eobRun > 0)
        {
            for (; k <= se; k++)
            {
                ref short coef = ref block[zz[k] & 63];
                if (coef != 0)
                {
                    if (count < 1)
                    {
                        _bits = bits;
                        _count = count;
                        _pos = pos;
                        Fill();
                        bits = _bits;
                        count = _count;
                        pos = _pos;
                        fastEnd = _marker == 0 ? _end - 8 : -1;
                    }

                    int bit = (int)(bits >> 63);
                    bits <<= 1;
                    count--;
                    if (bit != 0 && (coef & p1) == 0)
                    {
                        coef += (short)(coef >= 0 ? p1 : m1);
                    }
                }
            }

            eobRun--;
        }

        _bits = bits;
        _count = count;
        _pos = pos;
    }
}
