using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// PNG scanline filters (W3C PNG 3rd edition, section 9). Rows passed in are the filtered or raw bytes without the leading
/// filter-type byte; callers validate the filter type and that <c>prev</c> has the same length as the row.
/// </summary>
internal static class PngFilter
{
    /// <summary>Reverses a filter in place. <paramref name="prev"/> is the previous unfiltered row (all zero for the first row).</summary>
    public static void Unfilter(int filter, Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        switch (filter)
        {
            case 1:
                UnSub(row, bpp);
                break;
            case 2:
                UnUp(row, prev);
                break;
            case 3:
                UnAverage(row, prev, bpp);
                break;
            case 4:
                UnPaeth(row, prev, bpp);
                break;
        }
    }

    internal static void UnSub(Span<byte> row, int bpp)
    {
        if ((bpp == 3 || bpp == 4) && Vector128.IsHardwareAccelerated && row.Length >= 16)
        {
            UnSubVector(row, bpp);
            return;
        }

        UnSubScalar(row, bpp);
    }

    // Sub is a running per-channel sum along the row, so a block of pixels is a log-step prefix sum (byte shifts and adds)
    // plus the previous block's last pixel. Byte adds wrap, which is the filter's arithmetic modulo 256.
    internal static void UnSubVector(Span<byte> row, int bpp)
    {
        int n = row.Length;
        int i = 0;
        if (bpp == 4)
        {
            Vector128<byte> shl4 = Vector128.Create(0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11).AsByte();
            Vector128<byte> shl8 = Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7).AsByte();
            Vector128<byte> last = Vector128.Create(12, 13, 14, 15, 12, 13, 14, 15, 12, 13, 14, 15, 12, 13, 14, 15).AsByte();
            Vector128<byte> carry = Vector128<byte>.Zero;
            for (; i + 16 <= n; i += 16)
            {
                Vector128<byte> v = Vector128.Create<byte>(row.Slice(i, 16));
                v += Vector128.Shuffle(v, shl4);
                v += Vector128.Shuffle(v, shl8);
                v += carry;
                v.CopyTo(row.Slice(i, 16));
                carry = Vector128.Shuffle(v, last);
            }
        }
        else
        {
            Vector128<byte> shl3 = Vector128.Create(0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12).AsByte();
            Vector128<byte> shl6 = Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9).AsByte();
            Vector128<byte> shl12 = Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3).AsByte();
            Vector128<byte> last = Vector128.Create(9, 10, 11, 9, 10, 11, 9, 10, 11, 9, 10, 11, 9, 10, 11, 9).AsByte();
            Vector128<byte> carry = Vector128<byte>.Zero;
            // 16 bytes are read but only the 12 that hold four whole pixels are stored; the next block starts there.
            for (; i + 16 <= n; i += 12)
            {
                Vector128<byte> v = Vector128.Create<byte>(row.Slice(i, 16));
                v += Vector128.Shuffle(v, shl3);
                v += Vector128.Shuffle(v, shl6);
                v += Vector128.Shuffle(v, shl12);
                v += carry;
                BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(i, 8), v.AsUInt64().GetElement(0));
                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(i + 8, 4), v.AsUInt32().GetElement(2));
                carry = Vector128.Shuffle(v, last);
            }
        }

        for (; i < n; i++)
        {
            row[i] += row[i - bpp];
        }
    }

    internal static void UnSubScalar(Span<byte> row, int bpp)
    {
        if (bpp == 4 && row.Length >= 4)
        {
            byte a0 = row[0], a1 = row[1], a2 = row[2], a3 = row[3];
            for (int i = 4; i + 3 < row.Length; i += 4)
            {
                Span<byte> px = row.Slice(i, 4);
                a0 = px[0] += a0;
                a1 = px[1] += a1;
                a2 = px[2] += a2;
                a3 = px[3] += a3;
            }

            return;
        }

        if (bpp == 3 && row.Length >= 3)
        {
            byte a0 = row[0], a1 = row[1], a2 = row[2];
            for (int i = 3; i + 2 < row.Length; i += 3)
            {
                Span<byte> px = row.Slice(i, 3);
                a0 = px[0] += a0;
                a1 = px[1] += a1;
                a2 = px[2] += a2;
            }

            return;
        }

        for (int i = bpp; i < row.Length; i++)
        {
            row[i] += row[i - bpp];
        }
    }

    internal static void UnUp(Span<byte> row, ReadOnlySpan<byte> prev)
    {
        int i = 0;
        int n = row.Length;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<byte>.Count;
            for (; i <= n - w; i += w)
            {
                (new Vector<byte>(row.Slice(i)) + new Vector<byte>(prev.Slice(i))).CopyTo(row.Slice(i));
            }
        }

        for (; i < n; i++)
        {
            row[i] += prev[i];
        }
    }

    internal static void UnAverage(Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        int n = row.Length;
        if (bpp == 4 || bpp == 3)
        {
            // Keep the left neighbour in registers instead of re-reading it from the row.
            int a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            int i = 0;
            if (bpp == 4)
            {
                for (; i + 4 <= n; i += 4)
                {
                    Span<byte> px = row.Slice(i, 4);
                    ReadOnlySpan<byte> up = prev.Slice(i, 4);
                    a0 = px[0] += (byte)((a0 + up[0]) >> 1);
                    a1 = px[1] += (byte)((a1 + up[1]) >> 1);
                    a2 = px[2] += (byte)((a2 + up[2]) >> 1);
                    a3 = px[3] += (byte)((a3 + up[3]) >> 1);
                }
            }
            else
            {
                for (; i + 3 <= n; i += 3)
                {
                    Span<byte> px = row.Slice(i, 3);
                    ReadOnlySpan<byte> up = prev.Slice(i, 3);
                    a0 = px[0] += (byte)((a0 + up[0]) >> 1);
                    a1 = px[1] += (byte)((a1 + up[1]) >> 1);
                    a2 = px[2] += (byte)((a2 + up[2]) >> 1);
                }
            }

            return;
        }

        int head = Math.Min(bpp, n);
        for (int i = 0; i < head; i++)
        {
            row[i] += (byte)(prev[i] >> 1);
        }

        for (int i = head; i < n; i++)
        {
            row[i] += (byte)((row[i - bpp] + prev[i]) >> 1);
        }
    }

    internal static void UnPaeth(Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        if ((bpp == 3 || bpp == 4) && Vector128.IsHardwareAccelerated)
        {
            UnPaethVector(row, prev, bpp);
            return;
        }

        UnPaethScalar(row, prev, bpp);
    }

    // One pixel per step with the channels in the low lanes of a Vector128<short>. The recurrence runs through the left
    // neighbour, so pixels cannot be done side by side; the win is that all channels of a pixel share one instruction stream
    // and no per-channel branches or byte stores. Values stay in 0..255 after the mask, so 16-bit lanes never overflow
    // (|a+b-2c| <= 510).
    internal static void UnPaethVector(Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        int n = row.Length;
        prev = prev.Slice(0, n);
        Vector128<short> mask = Vector128.Create((short)0xFF);
        Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
        for (int i = 0; i + bpp <= n; i += bpp)
        {
            Vector128<short> x = Widen(LoadPixel(row, i, bpp));
            Vector128<short> b = Widen(LoadPixel(prev, i, bpp));
            a = (x + PaethSelect(a, b, c)) & mask;
            StorePixel(row, i, bpp, Narrow(a));
            c = b;
        }
    }

    private static Vector128<short> Widen(uint pixel) => Vector128.WidenLower(Vector128.CreateScalar(pixel).AsByte()).AsInt16();

    private static uint Narrow(Vector128<short> v) => Vector128.Narrow(v.AsUInt16(), v.AsUInt16()).AsUInt32().ToScalar();

    // A 3-byte pixel is read as 4 bytes when the row has one more byte; the extra byte only lands in a lane that is never stored.
    private static uint LoadPixel(ReadOnlySpan<byte> s, int i, int bpp)
    {
        if (bpp == 4)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i));
        }

        if (i + 4 <= s.Length)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i)) & 0xFFFFFF;
        }

        return (uint)(s[i] | (s[i + 1] << 8) | (s[i + 2] << 16));
    }

    private static void StorePixel(Span<byte> s, int i, int bpp, uint v)
    {
        if (bpp == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(i), v);
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(i), (ushort)v);
        s[i + 2] = (byte)(v >> 16);
    }

    internal static void UnPaethScalar(Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        int n = row.Length;
        if (bpp == 4 || bpp == 3)
        {
            int a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            int c0 = 0, c1 = 0, c2 = 0, c3 = 0;
            int i = 0;
            if (bpp == 4)
            {
                for (; i + 4 <= n; i += 4)
                {
                    Span<byte> px = row.Slice(i, 4);
                    ReadOnlySpan<byte> up = prev.Slice(i, 4);
                    int b0 = up[0], b1 = up[1], b2 = up[2], b3 = up[3];
                    a0 = px[0] += Paeth(a0, b0, c0);
                    a1 = px[1] += Paeth(a1, b1, c1);
                    a2 = px[2] += Paeth(a2, b2, c2);
                    a3 = px[3] += Paeth(a3, b3, c3);
                    c0 = b0;
                    c1 = b1;
                    c2 = b2;
                    c3 = b3;
                }
            }
            else
            {
                for (; i + 3 <= n; i += 3)
                {
                    Span<byte> px = row.Slice(i, 3);
                    ReadOnlySpan<byte> up = prev.Slice(i, 3);
                    int b0 = up[0], b1 = up[1], b2 = up[2];
                    a0 = px[0] += Paeth(a0, b0, c0);
                    a1 = px[1] += Paeth(a1, b1, c1);
                    a2 = px[2] += Paeth(a2, b2, c2);
                    c0 = b0;
                    c1 = b1;
                    c2 = b2;
                }
            }

            return;
        }

        int head = Math.Min(bpp, n);
        for (int i = 0; i < head; i++)
        {
            row[i] += prev[i];
        }

        for (int i = head; i < n; i++)
        {
            row[i] += Paeth(row[i - bpp], prev[i], prev[i - bpp]);
        }
    }

    // PNG 3rd ed. 9.4: the predictor picks the neighbour closest to a + b - c, preferring a, then b, then c on ties.
    internal static byte Paeth(int a, int b, int c)
    {
        int pa = b - c;
        int pb = a - c;
        int pc = pa + pb;
        pa = pa < 0 ? -pa : pa;
        pb = pb < 0 ? -pb : pb;
        pc = pc < 0 ? -pc : pc;
        return pa <= pb && pa <= pc ? (byte)a : pb <= pc ? (byte)b : (byte)c;
    }

    // ---- Forward filters, used by the encoder. dest has the same length as raw. ----

    public static void Apply(int filter, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prev, Span<byte> dest, int bpp)
    {
        if (filter is 1 or 3 or 4 && Vector128.IsHardwareAccelerated && raw.Length >= bpp + 16)
        {
            ApplyVector(filter, raw, prev, dest, bpp);
            return;
        }

        ApplyScalar(filter, raw, prev, dest, bpp);
    }

    // Forward Sub, Average and Paeth read only unfiltered bytes, so 16 output bytes are independent of each other.
    // Every output byte comes out identical to the scalar form.
    internal static void ApplyVector(int filter, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prev, Span<byte> dest, int bpp)
    {
        int n = raw.Length;
        prev = prev.Slice(0, n);
        dest = dest.Slice(0, n);
        int head = Math.Min(bpp, n);
        int i = head;
        switch (filter)
        {
            case 1:
                raw[..head].CopyTo(dest);
                for (; i + 16 <= n; i += 16)
                {
                    (Vector128.Create<byte>(raw.Slice(i, 16)) - Vector128.Create<byte>(raw.Slice(i - bpp, 16))).CopyTo(dest.Slice(i, 16));
                }

                for (; i < n; i++)
                {
                    dest[i] = (byte)(raw[i] - raw[i - bpp]);
                }

                break;
            case 3:
                for (int k = 0; k < head; k++)
                {
                    dest[k] = (byte)(raw[k] - (prev[k] >> 1));
                }

                Vector128<byte> low7 = Vector128.Create((byte)0x7F);
                for (; i + 16 <= n; i += 16)
                {
                    Vector128<byte> a = Vector128.Create<byte>(raw.Slice(i - bpp, 16));
                    Vector128<byte> b = Vector128.Create<byte>(prev.Slice(i, 16));
                    // floor((a + b) / 2) without widening: (a & b) + ((a ^ b) >> 1).
                    Vector128<byte> avg = (a & b) + ((Vector128.ShiftRightLogical((a ^ b).AsUInt16(), 1).AsByte()) & low7);
                    (Vector128.Create<byte>(raw.Slice(i, 16)) - avg).CopyTo(dest.Slice(i, 16));
                }

                for (; i < n; i++)
                {
                    dest[i] = (byte)(raw[i] - ((raw[i - bpp] + prev[i]) >> 1));
                }

                break;
            default:
                for (int k = 0; k < head; k++)
                {
                    dest[k] = (byte)(raw[k] - prev[k]);
                }

                for (; i + 16 <= n; i += 16)
                {
                    (Vector128<ushort> alo, Vector128<ushort> ahi) = Vector128.Widen(Vector128.Create<byte>(raw.Slice(i - bpp, 16)));
                    (Vector128<ushort> blo, Vector128<ushort> bhi) = Vector128.Widen(Vector128.Create<byte>(prev.Slice(i, 16)));
                    (Vector128<ushort> clo, Vector128<ushort> chi) = Vector128.Widen(Vector128.Create<byte>(prev.Slice(i - bpp, 16)));
                    Vector128<short> plo = PaethSelect(alo.AsInt16(), blo.AsInt16(), clo.AsInt16());
                    Vector128<short> phi = PaethSelect(ahi.AsInt16(), bhi.AsInt16(), chi.AsInt16());
                    Vector128<byte> pred = Vector128.Narrow(plo.AsUInt16(), phi.AsUInt16());
                    (Vector128.Create<byte>(raw.Slice(i, 16)) - pred).CopyTo(dest.Slice(i, 16));
                }

                for (; i < n; i++)
                {
                    dest[i] = (byte)(raw[i] - Paeth(raw[i - bpp], prev[i], prev[i - bpp]));
                }

                break;
        }
    }

    // The Paeth predictor on 16-bit lanes holding 0..255 (see Paeth for the rule and tie order).
    private static Vector128<short> PaethSelect(Vector128<short> a, Vector128<short> b, Vector128<short> c)
    {
        Vector128<short> pa = b - c;
        Vector128<short> pb = a - c;
        Vector128<short> pc = pa + pb;
        pa = Vector128.Abs(pa);
        pb = Vector128.Abs(pb);
        pc = Vector128.Abs(pc);
        Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
        Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
        return Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c));
    }

    internal static void ApplyScalar(int filter, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prev, Span<byte> dest, int bpp)
    {
        int n = raw.Length;
        int head = Math.Min(bpp, n);
        switch (filter)
        {
            case 1:
                raw[..head].CopyTo(dest);
                for (int i = head; i < n; i++)
                {
                    dest[i] = (byte)(raw[i] - raw[i - bpp]);
                }

                break;
            case 2:
                {
                    int i = 0;
                    if (Vector.IsHardwareAccelerated)
                    {
                        int w = Vector<byte>.Count;
                        for (; i <= n - w; i += w)
                        {
                            (new Vector<byte>(raw.Slice(i)) - new Vector<byte>(prev.Slice(i))).CopyTo(dest.Slice(i));
                        }
                    }

                    for (; i < n; i++)
                    {
                        dest[i] = (byte)(raw[i] - prev[i]);
                    }

                    break;
                }

            case 3:
                for (int i = 0; i < head; i++)
                {
                    dest[i] = (byte)(raw[i] - (prev[i] >> 1));
                }

                for (int i = head; i < n; i++)
                {
                    dest[i] = (byte)(raw[i] - ((raw[i - bpp] + prev[i]) >> 1));
                }

                break;
            case 4:
                for (int i = 0; i < head; i++)
                {
                    dest[i] = (byte)(raw[i] - prev[i]);
                }

                for (int i = head; i < n; i++)
                {
                    dest[i] = (byte)(raw[i] - Paeth(raw[i - bpp], prev[i], prev[i - bpp]));
                }

                break;
            default:
                raw.CopyTo(dest);
                break;
        }
    }

    /// <summary>
    /// The sum of absolute values of the bytes read as signed numbers (the minimum-sum-of-absolute-differences heuristic).
    /// -128 counts as 128 on every path.
    /// </summary>
    public static long SumAbs(ReadOnlySpan<byte> data)
    {
        int i = 0;
        long sum = 0;
        if (Avx2.IsSupported && data.Length >= 32)
        {
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            for (; i <= data.Length - 32; i += 32)
            {
                Vector256<byte> abs = Avx2.Abs(Vector256.Create(data.Slice(i, 32)).AsSByte());
                acc = Avx2.Add(acc, Avx2.SumAbsoluteDifferences(abs, Vector256<byte>.Zero).AsUInt64());
            }

            sum = (long)Vector256.Sum(acc);
        }

        return sum + SumAbsScalar(data.Slice(i));
    }

    internal static long SumAbsScalar(ReadOnlySpan<byte> data)
    {
        long sum = 0;
        foreach (byte b in data)
        {
            sum += b < 128 ? b : 256 - b;
        }

        return sum;
    }
}
