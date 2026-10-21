namespace OneImage.Kernels;

/// <summary>
/// sRGB transfer function (IEC 61966-2-1) and the lookup tables built from it. Used while the color unit's general
/// profile-based linearization is not wired in: untagged RGB and gray are treated as sRGB.
/// </summary>
internal static class SrgbTables
{
    /// <summary>Linear value of encoded <c>i / 255</c>.</summary>
    public static readonly float[] ToLinear8 = BuildToLinear(255);

    /// <summary>Encoded value <c>i / 255</c> itself (used when resampling in the encoded space).</summary>
    public static readonly float[] Identity8 = BuildIdentity();

    /// <summary>Linear value of encoded <c>i / 65535</c>.</summary>
    public static readonly float[] ToLinear16 = BuildToLinear(65535);

    /// <summary>Nearest 8-bit sRGB code for linear <c>i / 65535</c>.</summary>
    public static readonly byte[] FromLinear8 = BuildFromLinear8();

    /// <summary>sRGB decode of one value; odd-symmetric below 0 and continued above 1 so float data keeps its range.</summary>
    public static float Decode(float v)
    {
        float a = MathF.Abs(v);
        float r = a <= 0.04045f ? a / 12.92f : MathF.Pow((a + 0.055f) / 1.055f, 2.4f);
        return v < 0 ? -r : r;
    }

    /// <summary>sRGB encode of one linear value (inverse of <see cref="Decode"/>).</summary>
    public static float Encode(float v)
    {
        float a = MathF.Abs(v);
        float r = a <= 0.0031308f ? a * 12.92f : (1.055f * MathF.Pow(a, 1f / 2.4f)) - 0.055f;
        return v < 0 ? -r : r;
    }

    private static double DecodeD(double a) => a <= 0.04045 ? a / 12.92 : Math.Pow((a + 0.055) / 1.055, 2.4);

    private static double EncodeD(double a) => a <= 0.0031308 ? a * 12.92 : (1.055 * Math.Pow(a, 1 / 2.4)) - 0.055;

    private static float[] BuildToLinear(int max)
    {
        var t = new float[max + 1];
        for (int i = 0; i <= max; i++)
        {
            t[i] = (float)DecodeD((double)i / max);
        }

        return t;
    }

    private static float[] BuildIdentity()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            t[i] = i / 255f;
        }

        return t;
    }

    private static byte[] BuildFromLinear8()
    {
        var t = new byte[65536];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = (byte)Math.Clamp((int)Math.Round(EncodeD((double)i / 65535) * 255, MidpointRounding.AwayFromZero), 0, 255);
        }

        return t;
    }
}
