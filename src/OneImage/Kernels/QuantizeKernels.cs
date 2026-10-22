using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>Scan helpers for RGBA8 rows used by the quantizer and the GIF encoder. Callers pass spans whose length is a multiple of 4.</summary>
internal static class QuantizeKernels
{
    /// <summary>Returns whether any pixel has alpha below <paramref name="threshold"/> (1 to 255).</summary>
    public static bool HasAlphaBelow(ReadOnlySpan<byte> rgba, byte threshold)
    {
        int n = rgba.Length & ~3;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && n >= 32)
        {
            ref byte r = ref MemoryMarshal.GetReference(rgba);
            Vector256<byte> limit = Vector256.Create(threshold);
            Vector256<byte> alphaOnly = Vector256.Create(0xFF000000u).AsByte(); // byte 3 of every pixel
            for (; i <= n - 32; i += 32)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref r, (nuint)i);
                if ((Vector256.LessThan(v, limit) & alphaOnly) != Vector256<byte>.Zero)
                {
                    return true;
                }
            }
        }

        return HasAlphaBelowScalar(rgba.Slice(i, n - i), threshold);
    }

    /// <summary>Reference implementation of <see cref="HasAlphaBelow"/>.</summary>
    public static bool HasAlphaBelowScalar(ReadOnlySpan<byte> rgba, byte threshold)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] < threshold)
            {
                return true;
            }
        }

        return false;
    }
}
