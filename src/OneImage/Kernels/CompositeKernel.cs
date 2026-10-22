using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>Row kernels that blend an overlay row onto a backdrop row.</summary>
internal static class CompositeKernel
{
    /// <summary>The opacity as a 16.16 fixed-point factor (0..65536) used by the 8-bit kernels.</summary>
    public static uint OpacityFixed(float opacity) => (uint)Math.Clamp((int)MathF.Round(opacity * 65536f), 0, 65536);

    /// <summary>Exact rounded division of 0..65025 by 255.</summary>
    private static int Div255(int t)
    {
        t += 128;
        return (t + (t >> 8)) >> 8;
    }

    /// <summary>
    /// Scalar reference for opaque 8-bit RGB under straight-alpha RGBA: <c>a = round(overlayAlpha * opacity)</c>, then
    /// <c>out = round((overlay * a + backdrop * (255 - a)) / 255)</c> per channel. Within 1 of the real-valued result.
    /// </summary>
    public static void OverRgb8Scalar(ReadOnlySpan<byte> backdrop, ReadOnlySpan<byte> overlayRgba, Span<byte> destination, int pixels, uint opacity)
    {
        for (int x = 0; x < pixels; x++)
        {
            int a = (int)((((uint)overlayRgba[(4 * x) + 3] * opacity) + 32768u) >> 16);
            for (int c = 0; c < 3; c++)
            {
                destination[(3 * x) + c] = (byte)Div255((overlayRgba[(4 * x) + c] * a) + (backdrop[(3 * x) + c] * (255 - a)));
            }
        }
    }

    private static readonly Vector128<byte> ExpandRgb = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
    private static readonly Vector128<byte> PackRgb = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> AlphaPick = Vector128.Create((byte)3, 0x80, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 11, 0x80, 0x80, 0x80, 15, 0x80, 0x80, 0x80);

    /// <summary>Same arithmetic as <see cref="OverRgb8Scalar"/>, four pixels per step; the result is identical.</summary>
    public static void OverRgb8(ReadOnlySpan<byte> backdrop, ReadOnlySpan<byte> overlayRgba, Span<byte> destination, int pixels, uint opacity)
    {
        backdrop = backdrop[..(pixels * 3)];
        overlayRgba = overlayRgba[..(pixels * 4)];
        destination = destination[..(pixels * 3)];
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var op = Vector128.Create(opacity);
            var round = Vector128.Create(32768u);
            var c255 = Vector128.Create((ushort)255);
            var c128 = Vector128.Create((ushort)128);
            // Each step loads and stores 16 bytes at a 3-byte pixel stride, so it needs 4 spare bytes after its 4 pixels.
            for (; x + 6 <= pixels; x += 4)
            {
                Vector128<byte> ov = Vector128.Create(overlayRgba.Slice(4 * x, 16));
                Vector128<byte> bk = Vector128.Shuffle(Vector128.Create(backdrop.Slice(3 * x, 16)), ExpandRgb);
                Vector128<uint> a = Vector128.Shuffle(ov, AlphaPick).AsUInt32();
                a = Vector128.ShiftRightLogical((a * op) + round, 16);
                Vector128<byte> a8 = (a * Vector128.Create(0x010101u)).AsByte();
                Vector128<ushort> aLo = Vector128.WidenLower(a8), aHi = Vector128.WidenUpper(a8);
                Vector128<ushort> oLo = Vector128.WidenLower(ov), oHi = Vector128.WidenUpper(ov);
                Vector128<ushort> bLo = Vector128.WidenLower(bk), bHi = Vector128.WidenUpper(bk);
                Vector128<ushort> tLo = (oLo * aLo) + (bLo * (c255 - aLo)) + c128;
                Vector128<ushort> tHi = (oHi * aHi) + (bHi * (c255 - aHi)) + c128;
                tLo = Vector128.ShiftRightLogical(tLo + Vector128.ShiftRightLogical(tLo, 8), 8);
                tHi = Vector128.ShiftRightLogical(tHi + Vector128.ShiftRightLogical(tHi, 8), 8);
                Vector128<byte> r = Vector128.Shuffle(Vector128.Narrow(tLo, tHi), PackRgb);
                r.CopyTo(destination.Slice(3 * x, 16));
            }
        }

        if (x < pixels)
        {
            OverRgb8Scalar(backdrop[(3 * x)..], overlayRgba[(4 * x)..], destination[(3 * x)..], pixels - x, opacity);
        }
    }
}
