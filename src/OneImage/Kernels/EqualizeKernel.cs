using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Luminance of 8-bit RGBA pixels for histogram equalization. The reference is <see cref="LumaRgba8Scalar"/>: each channel is scaled to
/// 0..1 (multiplied by 1/255 or, with <c>divide</c>, divided by 255), multiplied by its Rec. 709 weight, the three products are added in
/// R, G, B order, the sum is scaled by 255 and rounded half to even. The vector paths run exactly the same IEEE operations lane by lane
/// (no fused multiply-add), so they return the same value for every pixel; tails and short rows use the scalar loop.
/// </summary>
internal static class EqualizeKernel
{
    private const float WeightR = 0.2126f;
    private const float WeightG = 0.7152f;
    private const float WeightB = 0.0722f;
    private const float Inv255 = 1f / 255f;

    /// <summary>Writes the rounded luminance (0..255) of <paramref name="width"/> RGBA pixels. Both spans must be large enough (4 bytes and 1 ushort per pixel).</summary>
    public static void LumaRgba8(ReadOnlySpan<byte> rgba, int width, Span<ushort> luma, bool divide)
    {
        if (rgba.Length < width * 4 || luma.Length < width)
        {
            throw new ArgumentException("Buffers are smaller than the row.");
        }

        int x = 0;
        if (Vector256.IsHardwareAccelerated && width >= 16)
        {
            x = Vector256Path(rgba, width, luma, divide);
        }
        else if (Vector128.IsHardwareAccelerated && width >= 8)
        {
            x = Vector128Path(rgba, width, luma, divide);
        }

        if (x < width)
        {
            LumaRgba8Scalar(rgba[(x * 4)..], width - x, luma[x..], divide);
        }
    }

    /// <summary>The reference implementation.</summary>
    public static void LumaRgba8Scalar(ReadOnlySpan<byte> rgba, int width, Span<ushort> luma, bool divide)
    {
        for (int i = 0; i < width; i++)
        {
            int p = i * 4;
            float r = rgba[p];
            float g = rgba[p + 1];
            float b = rgba[p + 2];
            float sum = divide
                ? (WeightR * (r / 255f)) + (WeightG * (g / 255f)) + (WeightB * (b / 255f))
                : (WeightR * (r * Inv255)) + (WeightG * (g * Inv255)) + (WeightB * (b * Inv255));
            luma[i] = (ushort)(int)MathF.Round(sum * 255f);
        }
    }

    private static int Vector256Path(ReadOnlySpan<byte> rgba, int width, Span<ushort> luma, bool divide)
    {
        ref byte src = ref MemoryMarshal.GetReference(rgba);
        ref ushort dst = ref MemoryMarshal.GetReference(luma);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<float> wr = Vector256.Create(WeightR);
        Vector256<float> wg = Vector256.Create(WeightG);
        Vector256<float> wb = Vector256.Create(WeightB);
        Vector256<float> inv = Vector256.Create(Inv255);
        Vector256<float> k255 = Vector256.Create(255f);
        int x = 0;
        for (; x + 16 <= width; x += 16)
        {
            Vector256<int> a = Luma8(Vector256.LoadUnsafe(ref src, (nuint)(x * 4)).AsUInt32(), mask, wr, wg, wb, inv, k255, divide);
            Vector256<int> b = Luma8(Vector256.LoadUnsafe(ref src, (nuint)((x + 8) * 4)).AsUInt32(), mask, wr, wg, wb, inv, k255, divide);
            Vector256.Narrow(a.AsUInt32(), b.AsUInt32()).StoreUnsafe(ref dst, (nuint)x);
        }

        return x;
    }

    // The channel values are masked to 0..255 and read as signed lanes: the unsigned 32-bit to float conversion has no
    // instruction before AVX-512 and is emulated. The sum is at most 255 (the weights add up to 1), so the final conversion
    // needs no saturation fix-up.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Luma8(Vector256<uint> px, Vector256<uint> mask, Vector256<float> wr, Vector256<float> wg, Vector256<float> wb, Vector256<float> inv, Vector256<float> k255, bool divide)
    {
        Vector256<float> r = Vector256.ConvertToSingle((px & mask).AsInt32());
        Vector256<float> g = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(px, 8) & mask).AsInt32());
        Vector256<float> b = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(px, 16) & mask).AsInt32());
        Vector256<float> sum;
        if (divide)
        {
            sum = Vector256.Add(Vector256.Add(Vector256.Multiply(wr, Vector256.Divide(r, k255)), Vector256.Multiply(wg, Vector256.Divide(g, k255))), Vector256.Multiply(wb, Vector256.Divide(b, k255)));
        }
        else
        {
            sum = Vector256.Add(Vector256.Add(Vector256.Multiply(wr, Vector256.Multiply(r, inv)), Vector256.Multiply(wg, Vector256.Multiply(g, inv))), Vector256.Multiply(wb, Vector256.Multiply(b, inv)));
        }

        return Vector256.ConvertToInt32Native(Vector256.Round(Vector256.Multiply(sum, k255)));
    }

    private static int Vector128Path(ReadOnlySpan<byte> rgba, int width, Span<ushort> luma, bool divide)
    {
        ref byte src = ref MemoryMarshal.GetReference(rgba);
        ref ushort dst = ref MemoryMarshal.GetReference(luma);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<float> wr = Vector128.Create(WeightR);
        Vector128<float> wg = Vector128.Create(WeightG);
        Vector128<float> wb = Vector128.Create(WeightB);
        Vector128<float> inv = Vector128.Create(Inv255);
        Vector128<float> k255 = Vector128.Create(255f);
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            Vector128<int> a = Luma4(Vector128.LoadUnsafe(ref src, (nuint)(x * 4)).AsUInt32(), mask, wr, wg, wb, inv, k255, divide);
            Vector128<int> b = Luma4(Vector128.LoadUnsafe(ref src, (nuint)((x + 4) * 4)).AsUInt32(), mask, wr, wg, wb, inv, k255, divide);
            Vector128.Narrow(a.AsUInt32(), b.AsUInt32()).StoreUnsafe(ref dst, (nuint)x);
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Luma4(Vector128<uint> px, Vector128<uint> mask, Vector128<float> wr, Vector128<float> wg, Vector128<float> wb, Vector128<float> inv, Vector128<float> k255, bool divide)
    {
        Vector128<float> r = Vector128.ConvertToSingle((px & mask).AsInt32());
        Vector128<float> g = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(px, 8) & mask).AsInt32());
        Vector128<float> b = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(px, 16) & mask).AsInt32());
        Vector128<float> sum;
        if (divide)
        {
            sum = Vector128.Add(Vector128.Add(Vector128.Multiply(wr, Vector128.Divide(r, k255)), Vector128.Multiply(wg, Vector128.Divide(g, k255))), Vector128.Multiply(wb, Vector128.Divide(b, k255)));
        }
        else
        {
            sum = Vector128.Add(Vector128.Add(Vector128.Multiply(wr, Vector128.Multiply(r, inv)), Vector128.Multiply(wg, Vector128.Multiply(g, inv))), Vector128.Multiply(wb, Vector128.Multiply(b, inv)));
        }

        return Vector128.ConvertToInt32Native(Vector128.Round(Vector128.Multiply(sum, k255)));
    }
}
