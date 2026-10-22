namespace OneImage.Ops.Quantize;

/// <summary>One error-diffusion kernel: where the quantization error of a pixel goes, as integer weights over a common divisor.</summary>
internal sealed class DiffusionKernel
{
    public DiffusionKernel(int divisor, params (int Dx, int Dy, int Weight)[] taps)
    {
        Divisor = divisor;
        Taps = taps;
        int rows = 0;
        int back = 0;
        int ahead = 0;
        foreach ((int dx, int dy, _) in taps)
        {
            rows = Math.Max(rows, dy);
            back = Math.Max(back, -dx);
            ahead = Math.Max(ahead, dx);
        }

        RowsBelow = rows;
        Left = back;
        Right = ahead;
        float[] f = new float[taps.Length];
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = taps[i].Weight / (float)divisor;
        }

        Coefficients = f;
    }

    /// <summary>Gets the sum the weights are divided by.</summary>
    public int Divisor { get; }

    /// <summary>Gets the taps: column offset, row offset (0 = same row, to the right only) and integer weight.</summary>
    public (int Dx, int Dy, int Weight)[] Taps { get; }

    /// <summary>Gets the weights as floats (weight / divisor).</summary>
    public float[] Coefficients { get; }

    /// <summary>Gets how many rows below the current one receive error.</summary>
    public int RowsBelow { get; }

    /// <summary>Gets the farthest column to the left that receives error.</summary>
    public int Left { get; }

    /// <summary>Gets the farthest column to the right that receives error.</summary>
    public int Right { get; }
}

/// <summary>The classic error-diffusion kernels and ordered-dither matrices, written out from their published weights.</summary>
internal static class DitherTables
{
    private static readonly DiffusionKernel s_floydSteinberg = new(16, (1, 0, 7), (-1, 1, 3), (0, 1, 5), (1, 1, 1));
    private static readonly DiffusionKernel s_atkinson = new(8, (1, 0, 1), (2, 0, 1), (-1, 1, 1), (0, 1, 1), (1, 1, 1), (0, 2, 1));
    private static readonly DiffusionKernel s_burkes = new(32, (1, 0, 8), (2, 0, 4), (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2));
    private static readonly DiffusionKernel s_jarvis = new(
        48,
        (1, 0, 7), (2, 0, 5),
        (-2, 1, 3), (-1, 1, 5), (0, 1, 7), (1, 1, 5), (2, 1, 3),
        (-2, 2, 1), (-1, 2, 3), (0, 2, 5), (1, 2, 3), (2, 2, 1));

    private static readonly DiffusionKernel s_sierra2 = new(16, (1, 0, 4), (2, 0, 3), (-2, 1, 1), (-1, 1, 2), (0, 1, 3), (1, 1, 2), (2, 1, 1));
    private static readonly DiffusionKernel s_sierra3 = new(
        32,
        (1, 0, 5), (2, 0, 3),
        (-2, 1, 2), (-1, 1, 4), (0, 1, 5), (1, 1, 4), (2, 1, 2),
        (-1, 2, 2), (0, 2, 3), (1, 2, 2));

    private static readonly DiffusionKernel s_sierraLite = new(4, (1, 0, 2), (-1, 1, 1), (0, 1, 1));
    private static readonly DiffusionKernel s_stevenson = new(
        200,
        (2, 0, 32),
        (-3, 1, 12), (-1, 1, 26), (1, 1, 30), (3, 1, 16),
        (-2, 2, 12), (0, 2, 26), (2, 2, 12),
        (-3, 3, 5), (-1, 3, 12), (1, 3, 12), (3, 3, 5));

    private static readonly DiffusionKernel s_stucki = new(
        42,
        (1, 0, 8), (2, 0, 4),
        (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2),
        (-2, 2, 1), (-1, 2, 2), (0, 2, 4), (1, 2, 2), (2, 2, 1));

    private static readonly int[] s_bayer2 = [0, 2, 3, 1];
    private static readonly int[] s_bayer4 = Double(s_bayer2, 2);
    private static readonly int[] s_bayer8 = Double(s_bayer4, 4);
    private static readonly int[] s_bayer16 = Double(s_bayer8, 8);
    private static readonly int[] s_ordered3 = [0, 5, 2, 7, 4, 8, 3, 6, 1];

    /// <summary>Returns the diffusion kernel for a mode, or null when the mode is not error diffusion.</summary>
    public static DiffusionKernel? Kernel(Dither dither) => dither switch
    {
        Dither.FloydSteinberg => s_floydSteinberg,
        Dither.Atkinson => s_atkinson,
        Dither.Burkes => s_burkes,
        Dither.JarvisJudiceNinke => s_jarvis,
        Dither.Sierra2 => s_sierra2,
        Dither.Sierra3 => s_sierra3,
        Dither.SierraLite => s_sierraLite,
        Dither.StevensonArce => s_stevenson,
        Dither.Stucki => s_stucki,
        _ => null,
    };

    /// <summary>Returns the threshold matrix (values 0 to size²-1, row-major) of an ordered mode, or null.</summary>
    public static int[]? Matrix(Dither dither, out int size)
    {
        switch (dither)
        {
            case Dither.Bayer2x2:
                size = 2;
                return s_bayer2;
            case Dither.Ordered3x3:
                size = 3;
                return s_ordered3;
            case Dither.Bayer4x4:
                size = 4;
                return s_bayer4;
            case Dither.Ordered:
            case Dither.Bayer8x8:
                size = 8;
                return s_bayer8;
            case Dither.Bayer16x16:
                size = 16;
                return s_bayer16;
            default:
                size = 0;
                return null;
        }
    }

    // Bayer(2n) from Bayer(n): [4B, 4B+2; 4B+3, 4B+1].
    private static int[] Double(int[] b, int n)
    {
        int m = n * 2;
        var r = new int[m * m];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int v = b[(y * n) + x] * 4;
                r[(y * m) + x] = v;
                r[(y * m) + x + n] = v + 2;
                r[((y + n) * m) + x] = v + 3;
                r[((y + n) * m) + x + n] = v + 1;
            }
        }

        return r;
    }
}
