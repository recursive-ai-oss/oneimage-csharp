using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Pixel-sized helpers of error diffusion: a packed RGBA pixel is widened to four floats in 0..1, receives a share of the error, and is
/// rounded back to bytes, with the same float operations in the same order as the scalar definition
/// <c>(byte)clamp(((b / 255f + e * c) * 255f) + 0.5f, 0, 255)</c> per channel, so results do not depend on the hardware.
/// </summary>
internal static class DitherKernel
{
    /// <summary>Four channels of a packed little-endian RGBA pixel as byte / 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Unit(uint pixel)
    {
        Vector128<uint> wide = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(pixel).AsByte()));
        return Vector128.ConvertToSingle(wide) / Vector128.Create(255f);
    }

    /// <summary>Adds <paramref name="share"/> (already multiplied by the tap weight) to a pixel and rounds the channels back to bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddShare(uint pixel, Vector128<float> share)
    {
        Vector128<float> t = ((Unit(pixel) + share) * Vector128.Create(255f)) + Vector128.Create(0.5f);
        t = Vector128.Min(Vector128.Max(t, Vector128<float>.Zero), Vector128.Create(255f));
        Vector128<uint> u = Vector128.ConvertToUInt32(t);
        Vector128<ushort> n = Vector128.Narrow(u, u);
        return Vector128.Narrow(n, n).AsUInt32().ToScalar();
    }
}
