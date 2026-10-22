using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>Row kernels for the brush paint step.</summary>
internal static class BrushKernel
{
    /// <summary>
    /// Converts straight RGBA floats (nominally 0..1) to 8-bit RGBA: <c>round-half-up(v * 255)</c>, saturating, NaN to 0. The
    /// vector path gives exactly the bytes of <see cref="RgbaF32ToRgba8Scalar"/>. <paramref name="source"/> must hold
    /// 4 floats per pixel and <paramref name="destination"/> the same number of bytes.
    /// </summary>
    public static void RgbaF32ToRgba8(ReadOnlySpan<float> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<float> scale = Vector128.Create(255f), half = Vector128.Create(0.5f);
            Vector128<int> zero = Vector128<int>.Zero, max = Vector128.Create(255);
            for (; i + 16 <= source.Length; i += 16)
            {
                Vector128<int> a = Q(Vector128.Create(source.Slice(i, 4)), scale, half, zero, max);
                Vector128<int> b = Q(Vector128.Create(source.Slice(i + 4, 4)), scale, half, zero, max);
                Vector128<int> c = Q(Vector128.Create(source.Slice(i + 8, 4)), scale, half, zero, max);
                Vector128<int> d = Q(Vector128.Create(source.Slice(i + 12, 4)), scale, half, zero, max);
                Vector128<short> ab = Vector128.Narrow(a, b);
                Vector128<short> cd = Vector128.Narrow(c, d);
                Vector128.Narrow(ab.AsUInt16(), cd.AsUInt16()).CopyTo(destination.Slice(i, 16));
            }
        }

        RgbaF32ToRgba8Scalar(source[i..], destination[i..]);
    }

    private static Vector128<int> Q(Vector128<float> v, Vector128<float> scale, Vector128<float> half, Vector128<int> zero, Vector128<int> max)
    {
        // Saturating conversion first (NaN becomes 0), then clamp as integers, exactly as the scalar reference does.
        Vector128<int> n = Vector128.ConvertToInt32((v * scale) + half);
        return Vector128.Min(Vector128.Max(n, zero), max);
    }

    /// <summary>The reference for <see cref="RgbaF32ToRgba8"/>.</summary>
    public static void RgbaF32ToRgba8Scalar(ReadOnlySpan<float> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            int n = (int)((source[i] * 255f) + 0.5f);
            destination[i] = (byte)(n < 0 ? 0 : n > 255 ? 255 : n);
        }
    }
}
