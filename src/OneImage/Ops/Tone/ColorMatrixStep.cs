using System.Numerics;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Applies a <see cref="ColorMatrix"/> to straight (non-premultiplied) color and alpha, on the stored values (no transfer curve).
/// Gray input becomes RGB; an input without alpha gains straight alpha when the matrix produces anything but 1.
/// </summary>
internal sealed class ColorMatrixStep : ToneStep
{
    private const float Limit = 1e6f;

    private readonly ColorMatrix _matrix;
    private readonly float[] _m = new float[20];
    private readonly bool _alphaIdentity;
    private readonly bool _addsAlpha;

    public ColorMatrixStep(ColorMatrix matrix)
    {
        matrix.CopyTo(_m);
        foreach (float v in _m)
        {
            // A bound keeps every sum finite, so no NaN can reach the integer conversion.
            if (!(MathF.Abs(v) <= Limit))
            {
                throw new ArgumentOutOfRangeException(nameof(matrix), "Matrix values must be finite and at most 1,000,000 in magnitude.");
            }
        }

        _matrix = matrix;
        _alphaIdentity = _m[3] == 0f && _m[7] == 0f && _m[11] == 0f && _m[15] == 1f && _m[19] == 0f;

        // For an opaque input the alpha output is 0*R + 0*G + 0*B + M44 + M54 when the first three weights are zero.
        float constant = _m[15] + _m[19];
        _addsAlpha = !(_m[3] == 0f && _m[7] == 0f && _m[11] == 0f && constant >= 1f);
    }

    public override string Name => "color_matrix";

    public override OpParameter[] Parameters() => [P("matrix", _matrix.ToParameter())];

    public override bool IsNoOp(PixelFormat input) => _matrix.IsIdentity;

    public override bool ModifiesAlpha => !_alphaIdentity;

    public override PixelFormat Output(PixelFormat input)
    {
        AlphaMode alpha = input.HasAlpha ? input.Alpha : _addsAlpha ? AlphaMode.Straight : AlphaMode.None;
        return new PixelFormat(ColorModel.Rgb, input.Sample, alpha);
    }

    public override bool TryApplyBytes(ReadOnlySpan<byte> source, Span<byte> destination, PixelFormat input, int width)
    {
        if (input.Sample != SampleType.U8 || input.Model != ColorModel.Rgb || input.Alpha == AlphaMode.Premultiplied)
        {
            return false;
        }

        PixelFormat output = Output(input);
        bool compute = input.HasAlpha ? !_alphaIdentity : _addsAlpha;
        ColorMatrixKernel.Apply8(source, destination, width, input.ChannelCount, output.ChannelCount, compute, _m);
        return true;
    }

    public override void Apply(ref ToneRow row)
    {
        if (row.Colors == 1)
        {
            row.Plane(0).CopyTo(row.Plane(1));
            row.Plane(0).CopyTo(row.Plane(2));
            row.Colors = 3;
        }

        bool hasAlpha = row.HasAlpha;
        bool produceAlpha = hasAlpha ? !_alphaIdentity : _addsAlpha;
        Span<float> rs = row.Plane(0);
        Span<float> gs = row.Plane(1);
        Span<float> bs = row.Plane(2);
        Span<float> a = row.Plane(3);
        if (!hasAlpha)
        {
            a.Fill(1f);
        }

        float[] m = _m;
        int n = rs.Length;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vn = Vector<float>.Count;
            var m11 = new Vector<float>(m[0]);
            var m12 = new Vector<float>(m[1]);
            var m13 = new Vector<float>(m[2]);
            var m14 = new Vector<float>(m[3]);
            var m21 = new Vector<float>(m[4]);
            var m22 = new Vector<float>(m[5]);
            var m23 = new Vector<float>(m[6]);
            var m24 = new Vector<float>(m[7]);
            var m31 = new Vector<float>(m[8]);
            var m32 = new Vector<float>(m[9]);
            var m33 = new Vector<float>(m[10]);
            var m34 = new Vector<float>(m[11]);
            var m41 = new Vector<float>(m[12]);
            var m42 = new Vector<float>(m[13]);
            var m43 = new Vector<float>(m[14]);
            var m44 = new Vector<float>(m[15]);
            var m51 = new Vector<float>(m[16]);
            var m52 = new Vector<float>(m[17]);
            var m53 = new Vector<float>(m[18]);
            var m54 = new Vector<float>(m[19]);
            for (; i <= n - vn; i += vn)
            {
                var r = new Vector<float>(rs[i..]);
                var g = new Vector<float>(gs[i..]);
                var b = new Vector<float>(bs[i..]);
                var al = new Vector<float>(a[i..]);
                (((r * m11) + (g * m21) + (b * m31) + (al * m41)) + m51).CopyTo(rs[i..]);
                (((r * m12) + (g * m22) + (b * m32) + (al * m42)) + m52).CopyTo(gs[i..]);
                (((r * m13) + (g * m23) + (b * m33) + (al * m43)) + m53).CopyTo(bs[i..]);
                if (produceAlpha)
                {
                    Vector.Min(Vector.Max((((r * m14) + (g * m24) + (b * m34) + (al * m44)) + m54), Vector<float>.Zero), Vector<float>.One).CopyTo(a[i..]);
                }
            }
        }

        for (; i < n; i++)
        {
            float r = rs[i];
            float g = gs[i];
            float b = bs[i];
            float al = a[i];
            rs[i] = ((r * m[0]) + (g * m[4]) + (b * m[8]) + (al * m[12])) + m[16];
            gs[i] = ((r * m[1]) + (g * m[5]) + (b * m[9]) + (al * m[13])) + m[17];
            bs[i] = ((r * m[2]) + (g * m[6]) + (b * m[10]) + (al * m[14])) + m[18];
            if (produceAlpha)
            {
                float v = ((r * m[3]) + (g * m[7]) + (b * m[11]) + (al * m[15])) + m[19];
                a[i] = MathF.Min(MathF.Max(v, 0f), 1f);
            }
        }

        row.HasAlpha = hasAlpha || _addsAlpha;
    }
}
