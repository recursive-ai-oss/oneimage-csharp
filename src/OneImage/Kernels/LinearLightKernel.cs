namespace OneImage.Kernels;

/// <summary>
/// Row conversion between 8-bit sRGB-encoded pixels and straight linear-light floats, for blending only the covered pixels of a
/// drawing operation in linear light. Decoding is a 256-entry table; encoding picks the 8-bit code whose rounding interval
/// contains the linear value, so it equals "encode exactly, then round half up" without a power function per sample.
/// </summary>
internal static class LinearLightKernel
{
    private const int GuessSteps = 4096;

    // Code i+1 starts at the linear value of the encoded point (i + 0.5) / 255: below it, rounding gives i or less.
    private static readonly float[] Thresholds = BuildThresholds();

    // For each 1/4096 bucket, how many thresholds lie at or below the bucket's lower edge (a lower bound for the code).
    private static readonly byte[] Guess = BuildGuess();

    /// <summary>The 8-bit sRGB code for a linear value: NaN and values at or below 0 give 0, values at or above 1 give 255.</summary>
    public static byte EncodeSample(float linear)
    {
        if (!(linear > 0f))
        {
            return 0;
        }

        if (linear >= 1f)
        {
            return 255;
        }

        float[] thr = Thresholds;
        int g = Guess[(int)(linear * GuessSteps)];
        while (g < 255 && linear >= thr[g])
        {
            g++;
        }

        return (byte)g;
    }

    /// <summary>
    /// Decodes <paramref name="count"/> pixels of <paramref name="colors"/> color bytes (sRGB) plus an optional alpha byte into
    /// straight floats: linear color, alpha as <c>byte / 255</c>.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> pixels, int colors, bool alpha, Span<float> straight, int count)
    {
        float[] table = SrgbTables.ToLinear8;
        int ch = alpha ? colors + 1 : colors;
        ReadOnlySpan<byte> src = pixels[..(count * ch)];
        Span<float> dst = straight[..(count * ch)];
        for (int i = 0; i < src.Length; i += ch)
        {
            for (int k = 0; k < colors; k++)
            {
                dst[i + k] = table[src[i + k]];
            }

            if (alpha)
            {
                dst[i + colors] = src[i + colors] * (1f / 255f);
            }
        }
    }

    /// <summary>The inverse of <see cref="Decode"/>: sRGB codes for linear color, alpha rounded half up, all saturating.</summary>
    public static void Encode(ReadOnlySpan<float> straight, int colors, bool alpha, Span<byte> pixels, int count)
    {
        int ch = alpha ? colors + 1 : colors;
        ReadOnlySpan<float> src = straight[..(count * ch)];
        Span<byte> dst = pixels[..(count * ch)];
        for (int i = 0; i < src.Length; i += ch)
        {
            for (int k = 0; k < colors; k++)
            {
                dst[i + k] = EncodeSample(src[i + k]);
            }

            if (alpha)
            {
                float v = (src[i + colors] * 255f) + 0.5f;
                dst[i + colors] = !(v > 0f) ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
            }
        }
    }

    private static float[] BuildThresholds()
    {
        var t = new float[255];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = SrgbTransfer.DecodeOne((i + 0.5f) / 255f);
        }

        return t;
    }

    private static byte[] BuildGuess()
    {
        var g = new byte[GuessSteps + 1];
        float[] t = BuildThresholds();
        int c = 0;
        for (int k = 0; k <= GuessSteps; k++)
        {
            float edge = k / (float)GuessSteps;
            while (c < t.Length && t[c] <= edge)
            {
                c++;
            }

            g[k] = (byte)c;
        }

        return g;
    }
}
