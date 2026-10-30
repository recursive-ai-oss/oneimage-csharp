namespace OneImage;

/// <summary>
/// The sRGB transfer curve (IEC 61966-2-1) as fast row functions for resampling and blur in linear light.
/// Decoding 8-bit values uses a 256-entry table (exact). Float decoding is the exact formula. Float encoding is a 16385-entry
/// table with linear interpolation, accurate to under 0.01 of an 8-bit step; use <see cref="EncodeExact"/> where that is not enough.
/// The table paths clamp to 0..1; the exact paths extend the curve symmetrically for negative values.
/// </summary>
internal static partial class SrgbTransfer
{
    private const int EncodeSize = 16384;
    private static readonly float[] s_decode8 = FromBase64(Decode8Data, 256);
    private static readonly float[] s_encode = FromBase64(EncodeData, EncodeSize + 1);

    /// <summary>The 8-bit decode table (read-only by convention; exposed for tests).</summary>
    internal static float[] Decode8Table => s_decode8;

    /// <summary>The float encode table (read-only by convention; exposed for tests).</summary>
    internal static float[] EncodeTable => s_encode;

    /// <summary>Converts 8-bit encoded values to linear floats (0..1).</summary>
    public static void Decode8(ReadOnlySpan<byte> src, Span<float> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = s_decode8[src[i]];
        }
    }

    /// <summary>Converts encoded floats to linear in place (exact formula).</summary>
    public static void Decode(Span<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = DecodeOne(values[i]);
        }
    }

    /// <summary>Converts linear floats to encoded in place using the interpolated table. Values are clamped to 0..1.</summary>
    public static void Encode(Span<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            if (!(v > 0f))
            {
                values[i] = 0f;
                continue;
            }

            if (v >= 1f)
            {
                values[i] = 1f;
                continue;
            }

            float pos = v * EncodeSize;
            int k = (int)pos;
            float t = pos - k;
            values[i] = s_encode[k] + ((s_encode[k + 1] - s_encode[k]) * t);
        }
    }

    /// <summary>Converts linear floats to encoded in place with the exact formula (no clamping; negative values mirror).</summary>
    public static void EncodeExact(Span<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = EncodeOne(values[i]);
        }
    }

    public static float DecodeOne(float v)
    {
        float a = MathF.Abs(v);
        float r = a <= 0.04045f ? a / 12.92f : DeterministicMath.PowF((a + 0.055f) / 1.055f, 2.4f);
        return v < 0f ? -r : r;
    }

    public static float EncodeOne(float v)
    {
        float a = MathF.Abs(v);
        float r = a <= 0.0031308f ? a * 12.92f : (1.055f * DeterministicMath.PowF(a, 1f / 2.4f)) - 0.055f;
        return v < 0f ? -r : r;
    }

    private static float[] FromBase64(string data, int count)
    {
        byte[] bytes = Convert.FromBase64String(data);
        if (bytes.Length != count * sizeof(float))
        {
            throw new InvalidOperationException("sRGB table data has the wrong size.");
        }

        var t = new float[count];
        for (int i = 0; i < count; i++)
        {
            t[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return t;
    }
}
