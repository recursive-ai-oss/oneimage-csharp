using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Bit-mask helpers for progressive JPEG refinement scans (T.81 G.1.2.3). A refinement scan reads one correction bit for
/// every coefficient that is already nonzero in the band, in zigzag order, between the symbols that place new coefficients.
/// Working on a 64-bit mask of the nonzero positions lets the decoder read all of those bits at once and apply them without a
/// data-dependent branch per coefficient. Masks use bit <c>63 - k</c> for zigzag index <c>k</c>, so the lowest index is the
/// highest bit and a count of leading zeros finds it. The scalar code is the reference; every path gives identical masks.
/// Callers pass blocks of 64 coefficients; kernels never parse.
/// </summary>
internal static class JpegRefineKernels
{
    // [byte index of the natural-order mask * 256 + byte value] -> the same positions as bits of a zigzag-order mask.
    private static readonly ulong[] s_toZigzag = BuildToZigzag();

    private static ulong[] BuildToZigzag()
    {
        // Inverse of the zigzag table: natural position -> zigzag index.
        ReadOnlySpan<byte> zz = Codecs.Jpeg.JpegConstants.Zigzag;
        var inv = new int[64];
        for (int k = 0; k < 64; k++)
        {
            inv[zz[k] & 63] = k;
        }

        var table = new ulong[8 * 256];
        for (int j = 0; j < 8; j++)
        {
            for (int v = 0; v < 256; v++)
            {
                ulong m = 0;
                for (int t = 0; t < 8; t++)
                {
                    int k = inv[(8 * j) + t];
                    if ((v >> t & 1) != 0 && k != 0)
                    {
                        m |= 1UL << (63 - k);
                    }
                }

                table[(j * 256) + v] = m;
            }
        }

        return table;
    }

    /// <summary>Gets whether the vector paths and bit-deposit instructions are worth using on this machine.</summary>
    internal static bool Accelerated => Vector128.IsHardwareAccelerated;

    /// <summary>
    /// Returns the positions of the nonzero AC coefficients of <paramref name="block"/> (64 shorts, natural order) as a mask in
    /// zigzag order (bit <c>63 - k</c> for zigzag index <c>k</c>, index 0, the DC coefficient, never set).
    /// </summary>
    public static ulong NonzeroZigzagMask(ReadOnlySpan<short> block)
    {
        block = block[..64];
        ulong natural;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<short> zero = Vector256<short>.Zero;
            Vector256<short> e0 = Vector256.Equals(Vector256.Create(block[..16]), zero);
            Vector256<short> e1 = Vector256.Equals(Vector256.Create(block.Slice(16, 16)), zero);
            Vector256<short> e2 = Vector256.Equals(Vector256.Create(block.Slice(32, 16)), zero);
            Vector256<short> e3 = Vector256.Equals(Vector256.Create(block.Slice(48, 16)), zero);
            ulong z = Vector256.Narrow(e0, e1).AsByte().ExtractMostSignificantBits();
            z |= (ulong)Vector256.Narrow(e2, e3).AsByte().ExtractMostSignificantBits() << 32;
            natural = ~z;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<short> zero = Vector128<short>.Zero;
            ulong z = 0;
            for (int i = 0; i < 8; i++)
            {
                z |= (ulong)Vector128.Equals(Vector128.Create(block.Slice(8 * i, 8)), zero).ExtractMostSignificantBits() << (8 * i);
            }

            natural = ~z;
        }
        else
        {
            natural = 0;
            for (int i = 0; i < 64; i++)
            {
                natural |= (block[i] != 0 ? 1UL : 0UL) << i;
            }
        }

        ulong[] t = s_toZigzag;
        return t[(int)(natural & 0xFF)]
            | t[256 + (int)((natural >> 8) & 0xFF)]
            | t[512 + (int)((natural >> 16) & 0xFF)]
            | t[768 + (int)((natural >> 24) & 0xFF)]
            | t[1024 + (int)((natural >> 32) & 0xFF)]
            | t[1280 + (int)((natural >> 40) & 0xFF)]
            | t[1536 + (int)((natural >> 48) & 0xFF)]
            | t[1792 + (int)(natural >> 56)];
    }

    /// <summary>Scatters the low bits of <paramref name="source"/> to the set bit positions of <paramref name="mask"/>, lowest to lowest (PDEP).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Deposit(ulong source, ulong mask)
    {
        if (Bmi2.X64.IsSupported)
        {
            return Bmi2.X64.ParallelBitDeposit(source, mask);
        }

        ulong result = 0;
        while (mask != 0)
        {
            ulong low = mask & (ulong)-(long)mask;
            if ((source & 1) != 0)
            {
                result |= low;
            }

            source >>= 1;
            mask ^= low;
        }

        return result;
    }

    /// <summary>
    /// Returns the zigzag index of the <paramref name="n"/>-th lowest-indexed set position (0-based) of <paramref name="mask"/>,
    /// which must have more than <paramref name="n"/> bits set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SelectIndex(ulong mask, int n)
    {
        // The lowest zigzag index is the highest bit, so the n-th from the top is the (count - 1 - n)-th from the bottom.
        int fromBottom = BitOperations.PopCount(mask) - 1 - n;
        return 63 - BitOperations.TrailingZeroCount(Deposit(1UL << fromBottom, mask));
    }
}
