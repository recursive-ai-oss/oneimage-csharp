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
