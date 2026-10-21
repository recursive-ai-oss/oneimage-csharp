namespace OneImage.Kernels;

/// <summary>
/// Lookup tables for resampling in linear light, built from the shared <see cref="SrgbTransfer"/> curve: 8- and 16-bit decode
/// tables and a 65536-entry table from linear 16-bit to the nearest 8-bit code. Untagged RGB and gray are treated as sRGB.
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

    /// <summary>sRGB decode of one value; see <see cref="SrgbTransfer.DecodeOne"/>.</summary>
    public static float Decode(float v) => SrgbTransfer.DecodeOne(v);

    /// <summary>sRGB encode of one linear value; see <see cref="SrgbTransfer.EncodeOne"/>.</summary>
    public static float Encode(float v) => SrgbTransfer.EncodeOne(v);

    private static float[] BuildToLinear(int max)
    {
        var t = new float[max + 1];
        for (int i = 0; i <= max; i++)
        {
            t[i] = SrgbTransfer.DecodeOne((float)i / max);
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
            t[i] = (byte)Math.Clamp((int)Math.Round(SrgbTransfer.EncodeOne((float)i / 65535) * 255, MidpointRounding.AwayFromZero), 0, 255);
        }

        return t;
    }
}
