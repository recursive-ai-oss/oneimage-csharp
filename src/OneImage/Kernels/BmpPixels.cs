using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Red/blue swaps between the byte orders BMP stores (blue, green, red[, alpha]) and the library's (red, green, blue[, alpha]).
/// The operation is its own inverse, so decode and encode share it. Callers pass whole pixels and a destination that is long
/// enough; the vector paths work on 16-byte blocks and leave the last few pixels to the scalar path.
/// </summary>
internal static class BmpPixels
{
    // Byte shuffles. Bytes past the end of the useful output are don't-care (they are overwritten or never read).
    private static readonly Vector128<byte> s_swap3 = Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 14, 13, 12, 15);
    private static readonly Vector128<byte> s_narrow4 = Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 3, 7, 11, 15);
    private static readonly Vector128<byte> s_swap4 = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    /// <summary>Swaps red and blue in 3-byte pixels (BGR to RGB or back).</summary>
    /// <param name="src">Source pixels, <paramref name="pixels"/> * 3 bytes.</param>
    /// <param name="dst">Destination, at least <paramref name="pixels"/> * 3 bytes. May not overlap the source partially.</param>
    /// <param name="pixels">Pixel count.</param>
    public static void Swap3(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        int n = pixels * 3;
        src = src[..n];
        dst = dst[..n];
        int i = 0;
        if (Vector128.IsHardwareAccelerated && n >= 16)
        {
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            Vector128<byte> mask = s_swap3;
            // Five pixels (15 bytes) per 16-byte block; the sixteenth byte is rewritten by the next block.
            for (; i <= n - 48; i += 45)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref s, (nuint)i);
                Vector128<byte> b = Vector128.LoadUnsafe(ref s, (nuint)(i + 15));
                Vector128<byte> c = Vector128.LoadUnsafe(ref s, (nuint)(i + 30));
                Vector128.Shuffle(a, mask).StoreUnsafe(ref d, (nuint)i);
                Vector128.Shuffle(b, mask).StoreUnsafe(ref d, (nuint)(i + 15));
                Vector128.Shuffle(c, mask).StoreUnsafe(ref d, (nuint)(i + 30));
            }

            for (; i <= n - 16; i += 15)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)i), mask).StoreUnsafe(ref d, (nuint)i);
            }

            i = i / 3 * 3;
        }

        Swap3Scalar(src[i..], dst[i..], (n - i) / 3);
    }

    /// <summary>Reference implementation of <see cref="Swap3"/>.</summary>
    public static void Swap3Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        for (int p = 0; p < pixels; p++)
        {
            byte x = src[p * 3];
            byte y = src[(p * 3) + 1];
            byte z = src[(p * 3) + 2];
            dst[p * 3] = z;
            dst[(p * 3) + 1] = y;
            dst[(p * 3) + 2] = x;
        }
    }

    /// <summary>Drops the fourth byte of 4-byte pixels and swaps red and blue (BGRA or BGRx to RGB, and RGBA to BGR).</summary>
    /// <param name="src">Source pixels, <paramref name="pixels"/> * 4 bytes.</param>
    /// <param name="dst">Destination, at least <paramref name="pixels"/> * 3 bytes.</param>
    /// <param name="pixels">Pixel count.</param>
    public static void Narrow4(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        src = src[..(pixels * 4)];
        dst = dst[..(pixels * 3)];
        int p = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            Vector128<byte> mask = s_narrow4;
            // Four pixels per block: 16 bytes in, 12 meaningful bytes out written as 16 (the last 4 are rewritten by the next block).
            for (; (p * 3) + 16 <= pixels * 3 && p + 4 <= pixels; p += 4)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(p * 4)), mask).StoreUnsafe(ref d, (nuint)(p * 3));
            }
        }

        Narrow4Scalar(src[(p * 4)..], dst[(p * 3)..], pixels - p);
    }

    /// <summary>Reference implementation of <see cref="Narrow4"/>.</summary>
    public static void Narrow4Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        for (int p = 0; p < pixels; p++)
        {
            byte x = src[p * 4];
            byte y = src[(p * 4) + 1];
            byte z = src[(p * 4) + 2];
            dst[p * 3] = z;
            dst[(p * 3) + 1] = y;
            dst[(p * 3) + 2] = x;
        }
    }

    /// <summary>Swaps red and blue in 4-byte pixels, keeping the fourth byte (BGRA to RGBA or back).</summary>
    /// <param name="src">Source pixels, <paramref name="pixels"/> * 4 bytes.</param>
    /// <param name="dst">Destination, at least <paramref name="pixels"/> * 4 bytes.</param>
    /// <param name="pixels">Pixel count.</param>
    public static void Swap4(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        int n = pixels * 4;
        src = src[..n];
        dst = dst[..n];
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            Vector128<byte> mask = s_swap4;
            for (; i <= n - 16; i += 16)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)i), mask).StoreUnsafe(ref d, (nuint)i);
            }
        }

        Swap4Scalar(src[i..], dst[i..], (n - i) / 4);
    }

    /// <summary>Reference implementation of <see cref="Swap4"/>.</summary>
    public static void Swap4Scalar(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        for (int p = 0; p < pixels; p++)
        {
            byte x = src[p * 4];
            byte y = src[(p * 4) + 1];
            byte z = src[(p * 4) + 2];
            byte w = src[(p * 4) + 3];
            dst[p * 4] = z;
            dst[(p * 4) + 1] = y;
            dst[(p * 4) + 2] = x;
            dst[(p * 4) + 3] = w;
        }
    }
}
