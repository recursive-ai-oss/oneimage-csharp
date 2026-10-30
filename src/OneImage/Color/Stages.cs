using OneImage.Kernels;

namespace OneImage;

/// <summary>One step of an exact (float) color pipeline. Applied to a whole row at a time so dispatch cost is per row, not per pixel.</summary>
internal abstract class Stage
{
    protected Stage(int inChannels, int outChannels)
    {
        In = inChannels;
        Out = outChannels;
    }

    public int In { get; }

    public int Out { get; }

    /// <summary>Transforms <paramref name="n"/> pixels from <paramref name="src"/> (stride <see cref="In"/>) to <paramref name="dst"/> (stride <see cref="Out"/>). The buffers never overlap.</summary>
    public abstract void Apply(ReadOnlySpan<float> src, Span<float> dst, int n);
}

/// <summary>Independent per-channel curves.</summary>
internal sealed class CurveStage : Stage
{
    private readonly ToneCurve[] _curves;

    public CurveStage(ToneCurve[] curves)
        : base(curves.Length, curves.Length)
    {
        _curves = curves;
    }

    public bool IsIdentity => _curves.All(c => c.IsIdentity);

    /// <summary>Returns a stage whose analytic curves are replaced by 16385-entry tables (linear interpolation), the form used for 8- and 16-bit buffers.</summary>
    public CurveStage Tabulated() => new(_curves.Select(c => c.Tabulate()).ToArray());

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        int ch = _curves.Length;
        for (int c = 0; c < ch; c++)
        {
            ToneCurve curve = _curves[c];
            for (int i = 0, p = c; i < n; i++, p += ch)
            {
                dst[p] = curve.Eval(src[p]);
            }
        }
    }
}

/// <summary>A 3x3 matrix plus offset: out = M * in + offset. Adjacent matrices are composed in double precision at build time.</summary>
internal sealed class MatrixStage : Stage
{
    private readonly float[] _f;

    public MatrixStage(ReadOnlySpan<double> m, ReadOnlySpan<double> offset)
        : base(3, 3)
    {
        M = m.ToArray();
        O = new double[3];
        for (int i = 0; i < 3 && i < offset.Length; i++)
        {
            O[i] = offset[i];
        }

        _f = new float[12];
        for (int i = 0; i < 9; i++)
        {
            _f[i] = (float)M[i];
        }

        for (int i = 0; i < 3; i++)
        {
            _f[9 + i] = (float)O[i];
        }
    }

    public double[] M { get; }

    public double[] O { get; }

    /// <summary>Returns the stage that applies <paramref name="first"/> and then <paramref name="second"/>.</summary>
    public static MatrixStage Compose(MatrixStage first, MatrixStage second)
    {
        var m = new double[9];
        var o = new double[3];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[(r * 3) + c] = (second.M[r * 3] * first.M[c]) + (second.M[(r * 3) + 1] * first.M[3 + c]) + (second.M[(r * 3) + 2] * first.M[6 + c]);
            }

            o[r] = (second.M[r * 3] * first.O[0]) + (second.M[(r * 3) + 1] * first.O[1]) + (second.M[(r * 3) + 2] * first.O[2]) + second.O[r];
        }

        return new MatrixStage(m, o);
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        float[] f = _f;
        for (int i = 0; i < n; i++)
        {
            int p = i * 3;
            float a = src[p], b = src[p + 1], c = src[p + 2];
            dst[p] = (f[0] * a) + (f[1] * b) + (f[2] * c) + f[9];
            dst[p + 1] = (f[3] * a) + (f[4] * b) + (f[5] * c) + f[10];
            dst[p + 2] = (f[6] * a) + (f[7] * b) + (f[8] * c) + f[11];
        }
    }
}

/// <summary>Per-channel scale and offset: out = in * scale + offset. Used for PCS encodings; three-channel instances fuse into matrices.</summary>
internal sealed class ScaleStage : Stage
{
    private readonly float[] _s;
    private readonly float[] _o;

    public ScaleStage(ReadOnlySpan<double> scale, ReadOnlySpan<double> offset)
        : base(scale.Length, scale.Length)
    {
        Scale = scale.ToArray();
        Offset = offset.ToArray();
        _s = new float[scale.Length];
        _o = new float[scale.Length];
        for (int i = 0; i < _s.Length; i++)
        {
            _s[i] = (float)scale[i];
            _o[i] = (float)offset[i];
        }
    }

    public double[] Scale { get; }

    public double[] Offset { get; }

    public MatrixStage ToMatrix() => new([Scale[0], 0, 0, 0, Scale[1], 0, 0, 0, Scale[2]], Offset);

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        int ch = _s.Length;
        for (int i = 0, p = 0; i < n; i++)
        {
            for (int c = 0; c < ch; c++, p++)
            {
                dst[p] = (src[p] * _s[c]) + _o[c];
            }
        }
    }
}

/// <summary>A lookup grid (CLUT) stage.</summary>
internal sealed class GridStage : Stage
{
    private readonly Grid _grid;

    public GridStage(Grid grid)
        : base(grid.Inputs, grid.Outputs)
    {
        _grid = grid;
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n) => GridKernel.Eval(_grid, src, dst, n);
}

/// <summary>PCS conversions between real XYZ (D50, Y of white = 1) and L*a*b* (L 0..100, a and b natural units).</summary>
internal sealed class XyzToLabStage : Stage
{
    public XyzToLabStage()
        : base(3, 3)
    {
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int p = i * 3;
            float fx = LabF(src[p] / (float)Pcs.D50X);
            float fy = LabF(src[p + 1]);
            float fz = LabF(src[p + 2] / (float)Pcs.D50Z);
            dst[p] = (116f * fy) - 16f;
            dst[p + 1] = 500f * (fx - fy);
            dst[p + 2] = 200f * (fy - fz);
        }
    }

    private static float LabF(float t) =>
        t > 216f / 24389f ? DeterministicMath.CbrtF(t) : ((24389f / 27f * t) + 16f) / 116f;
}

internal sealed class LabToXyzStage : Stage
{
    public LabToXyzStage()
        : base(3, 3)
    {
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int p = i * 3;
            float fy = (src[p] + 16f) / 116f;
            float fx = fy + (src[p + 1] / 500f);
            float fz = fy - (src[p + 2] / 200f);
            dst[p] = Inv(fx) * (float)Pcs.D50X;
            dst[p + 1] = Inv(fy);
            dst[p + 2] = Inv(fz) * (float)Pcs.D50Z;
        }
    }

    private static float Inv(float f)
    {
        float f3 = f * f * f;
        return f3 > 216f / 24389f ? f3 : ((116f * f) - 16f) * (27f / 24389f);
    }
}

/// <summary>Gray (relative luminance Y) to PCS XYZ: the D50 white scaled by Y.</summary>
internal sealed class GrayToXyzStage : Stage
{
    public GrayToXyzStage()
        : base(1, 3)
    {
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float y = src[i];
            dst[i * 3] = y * (float)Pcs.D50X;
            dst[(i * 3) + 1] = y;
            dst[(i * 3) + 2] = y * (float)Pcs.D50Z;
        }
    }
}

/// <summary>PCS XYZ to gray: takes the luminance Y.</summary>
internal sealed class XyzToGrayStage : Stage
{
    public XyzToGrayStage()
        : base(3, 1)
    {
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        for (int i = 0; i < n; i++)
        {
            dst[i] = src[(i * 3) + 1];
        }
    }
}

/// <summary>Clamps every channel to 0..1.</summary>
internal sealed class ClampStage : Stage
{
    public ClampStage(int channels)
        : base(channels, channels)
    {
    }

    public override void Apply(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        int total = n * In;
        for (int i = 0; i < total; i++)
        {
            float v = src[i];
            dst[i] = !(v > 0f) ? 0f : v > 1f ? 1f : v;
        }
    }
}

/// <summary>The profile connection space white point (D50) as used by ICC.</summary>
internal static class Pcs
{
    public const double D50X = 0.9642;
    public const double D50Y = 1.0;
    public const double D50Z = 0.8249;
}
