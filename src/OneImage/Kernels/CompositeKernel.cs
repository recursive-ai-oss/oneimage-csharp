using System.Runtime.InteropServices;
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

    /// <summary>
    /// Scalar reference for straight 8-bit RGBA over straight 8-bit RGBA, Normal blend and source-over, with the exact float operations the general path
    /// (convert to premultiplied float, <see cref="BlendKernel.Row"/>, convert back) performs, in the same order, so the result is identical to it:
    /// <c>out = backdrop·(1 − αs) + overlay</c> on premultiplied values, with <c>αs = overlayAlpha · opacity</c>, then divided by the result alpha and rounded half up.
    /// </summary>
    public static void OverRgba8Scalar(ReadOnlySpan<byte> backdrop, ReadOnlySpan<byte> overlay, Span<byte> destination, int pixels, float opacity)
    {
        for (int x = 0; x < pixels; x++)
        {
            int i = 4 * x;
            float oa = overlay[i + 3] / 255f;
            float ba = backdrop[i + 3] / 255f;
            float sa = oa * opacity;
            float fb = 1f + (-1f * sa);
            float outA = sa + (ba * fb);
            for (int c = 0; c < 3; c++)
            {
                float s = ((overlay[i + c] / 255f) * oa) * opacity;
                float d = (backdrop[i + c] / 255f) * ba;
                float v = s + (d * fb);
                destination[i + c] = ToByte(outA > 0f ? v / outA : 0f);
            }

            destination[i + 3] = ToByte(outA);
        }
    }

    private static byte ToByte(float unit)
    {
        float v = (unit * 255f) + 0.5f;
        return !(v > 0f) ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
    }

    /// <summary>The same arithmetic as <see cref="OverRgba8Scalar"/> for 8 (or 4) pixels per step on 32-bit float lanes; no fused multiply-add, so every hardware path gives the same bytes.</summary>
    public static void OverRgba8(ReadOnlySpan<byte> backdrop, ReadOnlySpan<byte> overlay, Span<byte> destination, int pixels, float opacity)
    {
        backdrop = backdrop[..(pixels * 4)];
        overlay = overlay[..(pixels * 4)];
        destination = destination[..(pixels * 4)];
        ReadOnlySpan<uint> b32 = MemoryMarshal.Cast<byte, uint>(backdrop);
        ReadOnlySpan<uint> o32 = MemoryMarshal.Cast<byte, uint>(overlay);
        Span<uint> d32 = MemoryMarshal.Cast<byte, uint>(destination);
        int x = 0;
        bool opacityInRange = opacity >= 0f && opacity <= 1f;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> op = Vector256.Create(opacity);
            Vector256<float> c255 = Vector256.Create(255f);
            Vector256<float> half = Vector256.Create(0.5f);
            Vector256<float> zero = Vector256<float>.Zero;
            Vector256<float> one = Vector256.Create(1f);
            Vector256<uint> mask = Vector256.Create(0xFFu);
            for (; x + 8 <= pixels; x += 8)
            {
                Vector256<uint> pb = Vector256.Create(b32.Slice(x, 8));
                Vector256<uint> po = Vector256.Create(o32.Slice(x, 8));
                Vector256<float> oa = Vector256.ConvertToSingle(Vector256.ShiftRightLogical(po, 24).AsInt32()) / c255;
                Vector256<float> sa = oa * op;
                Vector256<float> fb = one + (Vector256.Create(-1f) * sa);
                Vector256<uint> backAlpha = Vector256.ShiftRightLogical(pb, 24);
                Vector256<uint> packed;
                if (opacityInRange && Vector256.EqualsAll(backAlpha, Vector256.Create(255u)))
                {
                    // An opaque backdrop: its alpha is exactly 1, so the result alpha is 1·(1 − sa) + sa, which rounds to exactly 1 for every sa in 0..1
                    // (the stored value is 255 and every division by it is by 1). Only the colors need work.
                    packed = Vector256.Create(0xFF000000u);
                    for (int c = 0; c < 3; c++)
                    {
                        int sh = 8 * c;
                        Vector256<float> s = ((Vector256.ConvertToSingle((Vector256.ShiftRightLogical(po, sh) & mask).AsInt32()) / c255) * oa) * op;
                        Vector256<float> d = Vector256.ConvertToSingle((Vector256.ShiftRightLogical(pb, sh) & mask).AsInt32()) / c255;
                        packed |= Vector256.ShiftLeft(ToByte8(s + (d * fb), c255, half, zero), sh);
                    }
                }
                else
                {
                    Vector256<float> ba = Vector256.ConvertToSingle(backAlpha.AsInt32()) / c255;
                    Vector256<float> outA = sa + (ba * fb);
                    Vector256<float> positive = Vector256.GreaterThan(outA, zero);
                    packed = Vector256.ShiftLeft(ToByte8(outA, c255, half, zero), 24);
                    for (int c = 0; c < 3; c++)
                    {
                        int sh = 8 * c;
                        Vector256<float> s = ((Vector256.ConvertToSingle((Vector256.ShiftRightLogical(po, sh) & mask).AsInt32()) / c255) * oa) * op;
                        Vector256<float> d = (Vector256.ConvertToSingle((Vector256.ShiftRightLogical(pb, sh) & mask).AsInt32()) / c255) * ba;
                        Vector256<float> v = s + (d * fb);
                        Vector256<float> unit = Vector256.ConditionalSelect(positive, v / outA, zero);
                        packed |= Vector256.ShiftLeft(ToByte8(unit, c255, half, zero), sh);
                    }
                }

                packed.CopyTo(d32.Slice(x, 8));
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<float> op = Vector128.Create(opacity);
            Vector128<float> c255 = Vector128.Create(255f);
            Vector128<float> half = Vector128.Create(0.5f);
            Vector128<float> zero = Vector128<float>.Zero;
            Vector128<float> one = Vector128.Create(1f);
            Vector128<uint> mask = Vector128.Create(0xFFu);
            for (; x + 4 <= pixels; x += 4)
            {
                Vector128<uint> pb = Vector128.Create(b32.Slice(x, 4));
                Vector128<uint> po = Vector128.Create(o32.Slice(x, 4));
                Vector128<float> oa = Vector128.ConvertToSingle(Vector128.ShiftRightLogical(po, 24).AsInt32()) / c255;
                Vector128<float> sa = oa * op;
                Vector128<float> fb = one + (Vector128.Create(-1f) * sa);
                Vector128<uint> backAlpha = Vector128.ShiftRightLogical(pb, 24);
                Vector128<uint> packed;
                if (opacityInRange && Vector128.EqualsAll(backAlpha, Vector128.Create(255u)))
                {
                    // An opaque backdrop: its alpha is exactly 1, so the result alpha is 1·(1 − sa) + sa, which rounds to exactly 1 for every sa in 0..1
                    // (the stored value is 255 and every division by it is by 1). Only the colors need work.
                    packed = Vector128.Create(0xFF000000u);
                    for (int c = 0; c < 3; c++)
                    {
                        int sh = 8 * c;
                        Vector128<float> s = ((Vector128.ConvertToSingle((Vector128.ShiftRightLogical(po, sh) & mask).AsInt32()) / c255) * oa) * op;
                        Vector128<float> d = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(pb, sh) & mask).AsInt32()) / c255;
                        packed |= Vector128.ShiftLeft(ToByte4(s + (d * fb), c255, half, zero), sh);
                    }
                }
                else
                {
                    Vector128<float> ba = Vector128.ConvertToSingle(backAlpha.AsInt32()) / c255;
                    Vector128<float> outA = sa + (ba * fb);
                    Vector128<float> positive = Vector128.GreaterThan(outA, zero);
                    packed = Vector128.ShiftLeft(ToByte4(outA, c255, half, zero), 24);
                    for (int c = 0; c < 3; c++)
                    {
                        int sh = 8 * c;
                        Vector128<float> s = ((Vector128.ConvertToSingle((Vector128.ShiftRightLogical(po, sh) & mask).AsInt32()) / c255) * oa) * op;
                        Vector128<float> d = (Vector128.ConvertToSingle((Vector128.ShiftRightLogical(pb, sh) & mask).AsInt32()) / c255) * ba;
                        Vector128<float> v = s + (d * fb);
                        Vector128<float> unit = Vector128.ConditionalSelect(positive, v / outA, zero);
                        packed |= Vector128.ShiftLeft(ToByte4(unit, c255, half, zero), sh);
                    }
                }

                packed.CopyTo(d32.Slice(x, 4));
            }
        }

        if (x < pixels)
        {
            OverRgba8Scalar(backdrop[(4 * x)..], overlay[(4 * x)..], destination[(4 * x)..], pixels - x, opacity);
        }
    }

    // (unit·255 + 0.5) clamped to 0..255 and truncated: the scalar ToByte, with the same NaN-free inputs. Signed conversions: AVX2 has no unsigned ones, and every value here is far below 2^31.
    private static Vector256<uint> ToByte8(Vector256<float> unit, Vector256<float> c255, Vector256<float> half, Vector256<float> zero) =>
        Vector256.ConvertToInt32Native(Vector256.MinNative(Vector256.MaxNative((unit * c255) + half, zero), c255)).AsUInt32();

    private static Vector128<uint> ToByte4(Vector128<float> unit, Vector128<float> c255, Vector128<float> half, Vector128<float> zero) =>
        Vector128.ConvertToInt32Native(Vector128.MinNative(Vector128.MaxNative((unit * c255) + half, zero), c255)).AsUInt32();
}
