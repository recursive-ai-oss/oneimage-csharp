using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Chroma upsampling and color conversion kernels for JPEG decoding. The scalar code is the reference; the 8-bit
/// paths add Vector128 versions that must produce identical bytes. Samples are <see cref="byte"/> (8-bit) or
/// <see cref="ushort"/> (9..16-bit, scalar only). Callers validate spans before calling; kernels never parse.
/// </summary>
internal static class JpegSampling
{
    private const int FixCrR = (int)((1.40200 * 65536) + 0.5);
    private const int FixCbB = (int)((1.77200 * 65536) + 0.5);
    private const int FixCbG = (int)((0.34414 * 65536) + 0.5);
    private const int FixCrG = (int)((0.71414 * 65536) + 0.5);
    private const int Half = 1 << 15;

    private static readonly Vector128<byte> s_rgb0R = Mask(0, 0), s_rgb0G = Mask(0, 1), s_rgb0B = Mask(0, 2);

    private static readonly Vector128<byte> s_rgb1R = Mask(1, 0), s_rgb1G = Mask(1, 1), s_rgb1B = Mask(1, 2);

    private static readonly Vector128<byte> s_rgb2R = Mask(2, 0), s_rgb2G = Mask(2, 1), s_rgb2B = Mask(2, 2);

    private static bool VectorOk => Vector128.IsHardwareAccelerated;

    /// <summary>
    /// Builds the shuffle mask that places channel <paramref name="channel"/> of 16 pixels into output vector
    /// <paramref name="part"/> (0..2) of the 48-byte interleaved RGB run; other bytes are zeroed.
    /// </summary>
    private static Vector128<byte> Mask(int part, int channel)
    {
        Span<byte> m = stackalloc byte[16];
        for (int t = 0; t < 16; t++)
        {
            int position = (16 * part) + t;
            m[t] = position % 3 == channel ? (byte)(position / 3) : (byte)0x80;
        }

        return Vector128.Create((ReadOnlySpan<byte>)m);
    }

    /// <summary>Widens samples of <paramref name="precision"/> bits to the full 16-bit range; a no-op for byte samples.</summary>
    public static void ScaleTo16<T>(Span<T> samples, int precision)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(ushort))
        {
            ScaleTo16(MemoryMarshal.Cast<T, ushort>(samples), precision);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Interleaving and color conversion

    /// <summary>Interleaves three planes into RGB triples (no conversion).</summary>
    public static void InterleaveRgb<T>(ReadOnlySpan<T> r, ReadOnlySpan<T> g, ReadOnlySpan<T> b, Span<T> dst, int count)
        where T : unmanaged, IBinaryInteger<T>
    {
        Check(count, r.Length, g.Length, b.Length, dst.Length, 3);
        int i = 0;
        if (typeof(T) == typeof(byte) && VectorOk)
        {
            i = InterleaveRgbVector(MemoryMarshal.Cast<T, byte>(r), MemoryMarshal.Cast<T, byte>(g), MemoryMarshal.Cast<T, byte>(b), MemoryMarshal.Cast<T, byte>(dst), count);
        }

        for (; i < count; i++)
        {
            dst[3 * i] = r[i];
            dst[(3 * i) + 1] = g[i];
            dst[(3 * i) + 2] = b[i];
        }
    }

    /// <summary>Converts YCbCr planes to interleaved RGB (JFIF full-range conversion in 16-bit fixed point).</summary>
    /// <param name="y">Luma samples.</param>
    /// <param name="cb">Blue-difference samples.</param>
    /// <param name="cr">Red-difference samples.</param>
    /// <param name="dst">Output triples.</param>
    /// <param name="count">Pixels.</param>
    /// <param name="maxValue">255 for 8-bit data, 4095 for 12-bit.</param>
    public static void YccToRgb<T>(ReadOnlySpan<T> y, ReadOnlySpan<T> cb, ReadOnlySpan<T> cr, Span<T> dst, int count, int maxValue)
        where T : unmanaged, IBinaryInteger<T>
    {
        Check(count, y.Length, cb.Length, cr.Length, dst.Length, 3);
        int i = 0;
        if (typeof(T) == typeof(byte) && VectorOk)
        {
            i = YccToRgbVector(MemoryMarshal.Cast<T, byte>(y), MemoryMarshal.Cast<T, byte>(cb), MemoryMarshal.Cast<T, byte>(cr), MemoryMarshal.Cast<T, byte>(dst), count);
        }

        int center = (maxValue + 1) >> 1;
        for (; i < count; i++)
        {
            int yy = int.CreateTruncating(y[i]);
            int u = int.CreateTruncating(cb[i]) - center;
            int v = int.CreateTruncating(cr[i]) - center;
            dst[3 * i] = T.CreateTruncating(Math.Clamp(yy + (((FixCrR * v) + Half) >> 16), 0, maxValue));
            dst[(3 * i) + 1] = T.CreateTruncating(Math.Clamp(yy + (((-FixCbG * u) + ((-FixCrG * v) + Half)) >> 16), 0, maxValue));
            dst[(3 * i) + 2] = T.CreateTruncating(Math.Clamp(yy + (((FixCbB * u) + Half) >> 16), 0, maxValue));
        }
    }

    /// <summary>Interleaves four planes; with <paramref name="invert"/> every sample becomes <c>maxValue - sample</c> (Adobe CMYK stores inverted ink).</summary>
    public static void InterleaveCmyk<T>(ReadOnlySpan<T> c, ReadOnlySpan<T> m, ReadOnlySpan<T> y, ReadOnlySpan<T> k, Span<T> dst, int count, int maxValue, bool invert)
        where T : unmanaged, IBinaryInteger<T>
    {
        Check(count, c.Length, m.Length, y.Length, dst.Length, 4);
        if (k.Length < count)
        {
            throw new ArgumentException("Plane too short.");
        }

        T max = T.CreateTruncating(maxValue);
        for (int i = 0; i < count; i++)
        {
            dst[4 * i] = invert ? max - c[i] : c[i];
            dst[(4 * i) + 1] = invert ? max - m[i] : m[i];
            dst[(4 * i) + 2] = invert ? max - y[i] : y[i];
            dst[(4 * i) + 3] = invert ? max - k[i] : k[i];
        }
    }

    /// <summary>Converts YCCK planes to interleaved CMYK as the reference decoder does (C = max - R), then inverts when asked.</summary>
    public static void YcckToCmyk<T>(ReadOnlySpan<T> y, ReadOnlySpan<T> cb, ReadOnlySpan<T> cr, ReadOnlySpan<T> k, Span<T> dst, int count, int maxValue, bool invert)
        where T : unmanaged, IBinaryInteger<T>
    {
        Check(count, y.Length, cb.Length, cr.Length, dst.Length, 4);
        if (k.Length < count)
        {
            throw new ArgumentException("Plane too short.");
        }

        int center = (maxValue + 1) >> 1;
        for (int i = 0; i < count; i++)
        {
            int yy = int.CreateTruncating(y[i]);
            int u = int.CreateTruncating(cb[i]) - center;
            int v = int.CreateTruncating(cr[i]) - center;
            int r = Math.Clamp(yy + (((FixCrR * v) + Half) >> 16), 0, maxValue);
            int g = Math.Clamp(yy + (((-FixCbG * u) + ((-FixCrG * v) + Half)) >> 16), 0, maxValue);
            int b = Math.Clamp(yy + (((FixCbB * u) + Half) >> 16), 0, maxValue);
            int kk = int.CreateTruncating(k[i]);
            dst[4 * i] = T.CreateTruncating(invert ? r : maxValue - r);
            dst[(4 * i) + 1] = T.CreateTruncating(invert ? g : maxValue - g);
            dst[(4 * i) + 2] = T.CreateTruncating(invert ? b : maxValue - b);
            dst[(4 * i) + 3] = T.CreateTruncating(invert ? maxValue - kk : kk);
        }
    }

    /// <summary>Widens samples of <paramref name="precision"/> bits to the full 16-bit range, replicating the top bits.</summary>
    public static void ScaleTo16(Span<ushort> samples, int precision)
    {
        int up = 16 - precision;
        int down = (2 * precision) - 16;
        for (int i = 0; i < samples.Length; i++)
        {
            int v = samples[i];
            samples[i] = (ushort)((v << up) | (v >> down));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Upsampling. 'near' is the component row an output row sits closest to, 'far' the neighbouring row on the side
    // of the output row (the triangle filter weights them 3:1).

    /// <summary>Doubles a row horizontally with the triangle filter: out[2i] = (3 in[i] + in[i-1] + 1) / 4, out[2i+1] = (3 in[i] + in[i+1] + 2) / 4; the two end samples are copied.</summary>
    /// <param name="src">The component row; only the first <paramref name="n"/> samples are real.</param>
    /// <param name="n">Number of real samples (at least 2).</param>
    /// <param name="dst">Receives <c>2 * n</c> samples.</param>
    public static void UpsampleH2V1<T>(ReadOnlySpan<T> src, int n, Span<T> dst)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (n < 1 || src.Length < n || dst.Length < 2 * n)
        {
            throw new ArgumentException("Row too short.");
        }

        if (n == 1)
        {
            dst[0] = src[0];
            dst[1] = src[0];
            return;
        }

        dst[0] = src[0];
        dst[1] = T.CreateTruncating(((3 * int.CreateTruncating(src[0])) + int.CreateTruncating(src[1]) + 2) >> 2);
        int i = 1;
        if (typeof(T) == typeof(byte) && VectorOk)
        {
            i = H2V1Vector(MemoryMarshal.Cast<T, byte>(src), n, MemoryMarshal.Cast<T, byte>(dst));
        }

        for (; i < n - 1; i++)
        {
            int cur = 3 * int.CreateTruncating(src[i]);
            dst[2 * i] = T.CreateTruncating((cur + int.CreateTruncating(src[i - 1]) + 1) >> 2);
            dst[(2 * i) + 1] = T.CreateTruncating((cur + int.CreateTruncating(src[i + 1]) + 2) >> 2);
        }

        int last = 3 * int.CreateTruncating(src[n - 1]);
        dst[(2 * (n - 1))] = T.CreateTruncating((last + int.CreateTruncating(src[n - 2]) + 1) >> 2);
        dst[(2 * n) - 1] = src[n - 1];
    }

    /// <summary>Doubles a row horizontally and vertically-weights it against the neighbouring row (3:1) in one step.</summary>
    /// <param name="near">The closer component row.</param>
    /// <param name="far">The farther component row.</param>
    /// <param name="n">Number of real samples (at least 1).</param>
    /// <param name="dst">Receives <c>2 * n</c> samples.</param>
    public static void UpsampleH2V2<T>(ReadOnlySpan<T> near, ReadOnlySpan<T> far, int n, Span<T> dst)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (n < 1 || near.Length < n || far.Length < n || dst.Length < 2 * n)
        {
            throw new ArgumentException("Row too short.");
        }

        int sum0 = (3 * int.CreateTruncating(near[0])) + int.CreateTruncating(far[0]);
        dst[0] = T.CreateTruncating(((sum0 * 4) + 8) >> 4);
        if (n == 1)
        {
            dst[1] = T.CreateTruncating(((sum0 * 4) + 7) >> 4);
            return;
        }

        int sum1 = (3 * int.CreateTruncating(near[1])) + int.CreateTruncating(far[1]);
        dst[1] = T.CreateTruncating(((sum0 * 3) + sum1 + 7) >> 4);
        int i = 1;
        if (typeof(T) == typeof(byte) && VectorOk)
        {
            i = H2V2Vector(MemoryMarshal.Cast<T, byte>(near), MemoryMarshal.Cast<T, byte>(far), n, MemoryMarshal.Cast<T, byte>(dst));
        }

        // Scalar finish from column i: 'left' is the sum of column i-1, 'mid' of column i.
        int left = (3 * int.CreateTruncating(near[i - 1])) + int.CreateTruncating(far[i - 1]);
        int mid = (3 * int.CreateTruncating(near[i])) + int.CreateTruncating(far[i]);
        for (int c = i; c < n; c++)
        {
            dst[2 * c] = T.CreateTruncating(((mid * 3) + left + 8) >> 4);
            if (c == n - 1)
            {
                dst[(2 * c) + 1] = T.CreateTruncating(((mid * 4) + 7) >> 4);
            }
            else
            {
                int right = (3 * int.CreateTruncating(near[c + 1])) + int.CreateTruncating(far[c + 1]);
                dst[(2 * c) + 1] = T.CreateTruncating(((mid * 3) + right + 7) >> 4);
                left = mid;
                mid = right;
            }
        }
    }

    /// <summary>Weights a row against its vertical neighbour 3:1 (no horizontal change).</summary>
    /// <param name="near">The closer row.</param>
    /// <param name="far">The farther row.</param>
    /// <param name="n">Samples.</param>
    /// <param name="bias">1 for the upper output row of a pair, 2 for the lower (alternating rounding keeps the result unbiased).</param>
    /// <param name="dst">Receives <paramref name="n"/> samples.</param>
    public static void UpsampleH1V2<T>(ReadOnlySpan<T> near, ReadOnlySpan<T> far, int n, int bias, Span<T> dst)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (n < 0 || near.Length < n || far.Length < n || dst.Length < n)
        {
            throw new ArgumentException("Row too short.");
        }

        int i = 0;
        if (typeof(T) == typeof(byte) && VectorOk)
        {
            i = H1V2Vector(MemoryMarshal.Cast<T, byte>(near), MemoryMarshal.Cast<T, byte>(far), n, bias, MemoryMarshal.Cast<T, byte>(dst));
        }

        for (; i < n; i++)
        {
            dst[i] = T.CreateTruncating(((3 * int.CreateTruncating(near[i])) + int.CreateTruncating(far[i]) + bias) >> 2);
        }
    }

    /// <summary>Repeats every sample <paramref name="factor"/> times (box upsampling).</summary>
    public static void Replicate<T>(ReadOnlySpan<T> src, int n, int factor, Span<T> dst)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (n < 0 || factor < 1 || src.Length < n || (long)dst.Length < (long)n * factor)
        {
            throw new ArgumentException("Row too short.");
        }

        int i = 0;
        if (factor == 2 && typeof(T) == typeof(byte) && VectorOk)
        {
            i = ReplicateX2Vector(MemoryMarshal.Cast<T, byte>(src), n, MemoryMarshal.Cast<T, byte>(dst));
        }

        for (; i < n; i++)
        {
            T v = src[i];
            int o = i * factor;
            for (int j = 0; j < factor; j++)
            {
                dst[o + j] = v;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Vector128 implementations (8-bit)

    private static void Check(int count, int a, int b, int c, int dstLength, int channels)
    {
        if (count < 0 || a < count || b < count || c < count || (long)dstLength < (long)count * channels)
        {
            throw new ArgumentException("Row too short.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Load8(ReadOnlySpan<byte> s, int i) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(s), i))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load16(ReadOnlySpan<byte> s, int i) =>
        Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(s), (nuint)i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16(Span<byte> d, int i, Vector128<byte> v) =>
        v.StoreUnsafe(ref MemoryMarshal.GetReference(d), (nuint)i);

    private static int InterleaveRgbVector(ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, Span<byte> dst, int count)
    {
        int i = 0;
        for (; i + 16 <= count; i += 16)
        {
            WriteRgb16(dst, 3 * i, Load16(r, i), Load16(g, i), Load16(b, i));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRgb16(Span<byte> dst, int o, Vector128<byte> r, Vector128<byte> g, Vector128<byte> b)
    {
        Store16(dst, o, Vector128.Shuffle(r, s_rgb0R) | Vector128.Shuffle(g, s_rgb0G) | Vector128.Shuffle(b, s_rgb0B));
        Store16(dst, o + 16, Vector128.Shuffle(r, s_rgb1R) | Vector128.Shuffle(g, s_rgb1G) | Vector128.Shuffle(b, s_rgb1B));
        Store16(dst, o + 32, Vector128.Shuffle(r, s_rgb2R) | Vector128.Shuffle(g, s_rgb2G) | Vector128.Shuffle(b, s_rgb2B));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClampByte(Vector128<int> v) =>
        Vector128.Min(Vector128.Max(v, Vector128<int>.Zero), Vector128.Create(255));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PackBytes(Vector128<int> a, Vector128<int> b, Vector128<int> c, Vector128<int> d) =>
        Vector128.Narrow(
            Vector128.Narrow(ClampByte(a).AsUInt32(), ClampByte(b).AsUInt32()),
            Vector128.Narrow(ClampByte(c).AsUInt32(), ClampByte(d).AsUInt32()));

    private static int YccToRgbVector(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> dst, int count)
    {
        int i = 0;
        Vector128<int> vHalf = Vector128.Create(Half);
        Vector128<int> v128 = Vector128.Create(128);
        for (; i + 16 <= count; i += 16)
        {
            Vector128<byte> yb = Load16(y, i);
            Vector128<byte> cbb = Load16(cb, i);
            Vector128<byte> crb = Load16(cr, i);
            (Vector128<ushort> yl, Vector128<ushort> yh) = Vector128.Widen(yb);
            (Vector128<ushort> bl, Vector128<ushort> bh) = Vector128.Widen(cbb);
            (Vector128<ushort> rl, Vector128<ushort> rh) = Vector128.Widen(crb);
            (Vector128<uint> y0, Vector128<uint> y1) = Vector128.Widen(yl);
            (Vector128<uint> y2, Vector128<uint> y3) = Vector128.Widen(yh);
            (Vector128<uint> b0, Vector128<uint> b1) = Vector128.Widen(bl);
            (Vector128<uint> b2, Vector128<uint> b3) = Vector128.Widen(bh);
            (Vector128<uint> r0, Vector128<uint> r1) = Vector128.Widen(rl);
            (Vector128<uint> r2, Vector128<uint> r3) = Vector128.Widen(rh);

            Convert4(y0.AsInt32(), b0.AsInt32() - v128, r0.AsInt32() - v128, vHalf, out Vector128<int> rr0, out Vector128<int> gg0, out Vector128<int> bb0);
            Convert4(y1.AsInt32(), b1.AsInt32() - v128, r1.AsInt32() - v128, vHalf, out Vector128<int> rr1, out Vector128<int> gg1, out Vector128<int> bb1);
            Convert4(y2.AsInt32(), b2.AsInt32() - v128, r2.AsInt32() - v128, vHalf, out Vector128<int> rr2, out Vector128<int> gg2, out Vector128<int> bb2);
            Convert4(y3.AsInt32(), b3.AsInt32() - v128, r3.AsInt32() - v128, vHalf, out Vector128<int> rr3, out Vector128<int> gg3, out Vector128<int> bb3);
            WriteRgb16(dst, 3 * i, PackBytes(rr0, rr1, rr2, rr3), PackBytes(gg0, gg1, gg2, gg3), PackBytes(bb0, bb1, bb2, bb3));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert4(Vector128<int> y, Vector128<int> u, Vector128<int> v, Vector128<int> half, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b)
    {
        r = y + Vector128.ShiftRightArithmetic((v * FixCrR) + half, 16);
        b = y + Vector128.ShiftRightArithmetic((u * FixCbB) + half, 16);
        g = y + Vector128.ShiftRightArithmetic((u * -FixCbG) + (v * -FixCrG) + half, 16);
    }

    private static int H2V1Vector(ReadOnlySpan<byte> src, int n, Span<byte> dst)
    {
        int i = 1;
        Vector128<ushort> three = Vector128.Create((ushort)3);
        Vector128<ushort> one = Vector128.Create((ushort)1);
        Vector128<ushort> two = Vector128.Create((ushort)2);
        // Loads read 8 bytes at i-1, i and i+1; the last one ends at i+8.
        for (; i + 9 <= n; i += 8)
        {
            Vector128<ushort> cur = Load8(src, i) * three;
            Vector128<ushort> even = Vector128.ShiftRightLogical(cur + Load8(src, i - 1) + one, 2);
            Vector128<ushort> odd = Vector128.ShiftRightLogical(cur + Load8(src, i + 1) + two, 2);
            Store16(dst, 2 * i, (even | Vector128.ShiftLeft(odd, 8)).AsByte());
        }

        return i;
    }

    private static int H2V2Vector(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int n, Span<byte> dst)
    {
        int i = 1;
        Vector128<ushort> three = Vector128.Create((ushort)3);
        Vector128<ushort> eight = Vector128.Create((ushort)8);
        Vector128<ushort> seven = Vector128.Create((ushort)7);
        for (; i + 9 <= n; i += 8)
        {
            Vector128<ushort> cur = (Load8(near, i) * three) + Load8(far, i);
            Vector128<ushort> left = (Load8(near, i - 1) * three) + Load8(far, i - 1);
            Vector128<ushort> right = (Load8(near, i + 1) * three) + Load8(far, i + 1);
            Vector128<ushort> c3 = cur * three;
            Vector128<ushort> even = Vector128.ShiftRightLogical(c3 + left + eight, 4);
            Vector128<ushort> odd = Vector128.ShiftRightLogical(c3 + right + seven, 4);
            Store16(dst, 2 * i, (even | Vector128.ShiftLeft(odd, 8)).AsByte());
        }

        return i;
    }

    private static int H1V2Vector(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int n, int bias, Span<byte> dst)
    {
        int i = 0;
        Vector128<ushort> three = Vector128.Create((ushort)3);
        Vector128<ushort> b = Vector128.Create((ushort)bias);
        for (; i + 8 <= n; i += 8)
        {
            Vector128<ushort> v = Vector128.ShiftRightLogical((Load8(near, i) * three) + Load8(far, i) + b, 2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), i), Vector128.Narrow(v, v).AsUInt64().GetElement(0));
        }

        return i;
    }

    private static int ReplicateX2Vector(ReadOnlySpan<byte> src, int n, Span<byte> dst)
    {
        int i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Vector128<ushort> w = Load8(src, i);
            Store16(dst, 2 * i, (w | Vector128.ShiftLeft(w, 8)).AsByte());
        }

        return i;
    }
}
