using System.Numerics;

namespace OneImage.Drawing;

/// <summary>Maps a pixel center back into the gradient's own space through the inverse of a gradient transform, in double precision.</summary>
internal readonly struct GradientSpace
{
    private readonly double _a, _b, _c, _d, _e, _f;

    private GradientSpace(double a, double b, double c, double d, double e, double f)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
        _e = e;
        _f = f;
    }

    /// <summary>The inverse of <paramref name="m"/>, or <see langword="false"/> when it is not finite and invertible.</summary>
    public static bool TryCreate(Matrix3x2 m, out GradientSpace inverse)
    {
        inverse = default;
        double a = m.M11, b = m.M12, c = m.M21, d = m.M22, e = m.M31, f = m.M32;
        double det = (a * d) - (b * c);
        if (!double.IsFinite(det + e + f) || !(Math.Abs(det) > 1e-30))
        {
            return false;
        }

        double ia = d / det, ib = -b / det, ic = -c / det, id = a / det;
        inverse = new GradientSpace(ia, ib, ic, id, -((ia * e) + (ic * f)), -((ib * e) + (id * f)));
        if (!double.IsFinite(inverse._a + inverse._b + inverse._c + inverse._d + inverse._e + inverse._f))
        {
            inverse = default;
            return false;
        }

        return true;
    }

    // Row-vector convention of Matrix3x2: x' = x*M11 + y*M21 + M31, y' = x*M12 + y*M22 + M32.
    public void Map(double x, double y, out double u, out double v)
    {
        u = (x * _a) + (y * _c) + _e;
        v = (x * _b) + (y * _d) + _f;
    }

    /// <summary>The x coefficient of the mapped point: u = x * Ux + y * Uy + Uc.</summary>
    public (double Ux, double Uy, double Uc, double Vx, double Vy, double Vc) Coefficients => (_a, _c, _e, _b, _d, _f);
}

/// <summary>A linear gradient whose line is placed through a transform: position = projection of the pixel center, taken back into gradient space, on the line.</summary>
internal sealed class MatrixLinearGradientBrush : GradientBrush
{
    private readonly Vector2 _start, _end;
    private readonly Matrix3x2 _transform;
    private readonly double _tx, _ty, _tc;   // t = tx * px + ty * py + tc

    public MatrixLinearGradientBrush(Vector2 start, Vector2 end, ReadOnlySpan<ColorStop> stops, Matrix3x2 transform, GradientRepetition repetition, bool premultiplied)
        : base(stops, repetition, premultiplied)
    {
        CheckFinite(nameof(start), start.X, start.Y, end.X, end.Y);
        double dx = (double)end.X - start.X, dy = (double)end.Y - start.Y;
        double len2 = (dx * dx) + (dy * dy);
        if (!(len2 >= 1e-12))
        {
            throw new ArgumentException("The start and end of a linear gradient must differ.", nameof(end));
        }

        if (!GradientSpace.TryCreate(transform, out GradientSpace inv))
        {
            throw new ArgumentException("The transform must be finite and invertible.", nameof(transform));
        }

        _start = start;
        _end = end;
        _transform = transform;
        (double ux, double uy, double uc, double vx, double vy, double vc) = inv.Coefficients;

        // t = ((u - sx) * dx + (v - sy) * dy) / len2, with u and v affine in the pixel position.
        _tx = ((ux * dx) + (vx * dy)) / len2;
        _ty = ((uy * dx) + (vy * dy)) / len2;
        _tc = (((uc - start.X) * dx) + ((vc - start.Y) * dy)) / len2;
    }

    private MatrixLinearGradientBrush(MatrixLinearGradientBrush source, Vector4[] colors)
        : base(source, colors)
    {
        _start = source._start;
        _end = source._end;
        _transform = source._transform;
        _tx = source._tx;
        _ty = source._ty;
        _tc = source._tc;
    }

    protected override GradientBrush WithColors(Vector4[] colors) => new MatrixLinearGradientBrush(this, colors);

    protected override void Positions(int x, int y, Span<float> t)
    {
        double c = (_ty * (y + 0.5)) + _tc + (_tx * (x + 0.5));
        double step = _tx;
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = (float)(c + (step * i));
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [.. StopParameters("linear-gradient"), P("x1", _start.X), P("y1", _start.Y), P("x2", _end.X), P("y2", _end.Y), .. MatrixParameters(_transform)];

    internal static OpParameter[] MatrixParameters(Matrix3x2 m) =>
        [P("m11", m.M11), P("m12", m.M12), P("m21", m.M21), P("m22", m.M22), P("m31", m.M31), P("m32", m.M32)];
}

/// <summary>
/// A two-point conical gradient (a focal radial gradient): the position of a pixel is the t for which the pixel lies on the circle that
/// grows from (focal center, focal radius) at t = 0 to (center, radius) at t = 1; where two such circles qualify the one with the larger t
/// wins, and circles of negative radius never qualify. Pixels no circle reaches are transparent.
/// </summary>
internal sealed class ConicalGradientBrush : GradientBrush
{
    private readonly Vector2 _c0, _c1;
    private readonly float _r0, _r1;
    private readonly Matrix3x2 _transform;
    private readonly GradientSpace _inverse;
    private readonly double _cdx, _cdy, _dr, _a;

    public ConicalGradientBrush(Vector2 focal, float focalRadius, Vector2 center, float radius, ReadOnlySpan<ColorStop> stops, Matrix3x2 transform, GradientRepetition repetition, bool premultiplied)
        : base(stops, repetition, premultiplied)
    {
        CheckFinite(nameof(center), center.X, center.Y, focal.X, focal.Y);
        if (!(radius > 0f) || !float.IsFinite(radius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be positive and finite.");
        }

        if (!(focalRadius >= 0f) || !float.IsFinite(focalRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(focalRadius), "The focal radius must be zero or positive and finite.");
        }

        if (!GradientSpace.TryCreate(transform, out _inverse))
        {
            throw new ArgumentException("The transform must be finite and invertible.", nameof(transform));
        }

        _c0 = focal;
        _c1 = center;
        _r0 = focalRadius;
        _r1 = radius;
        _transform = transform;
        _cdx = (double)center.X - focal.X;
        _cdy = (double)center.Y - focal.Y;
        _dr = (double)radius - focalRadius;
        _a = (_cdx * _cdx) + (_cdy * _cdy) - (_dr * _dr);
    }

    private ConicalGradientBrush(ConicalGradientBrush source, Vector4[] colors)
        : base(source, colors)
    {
        _c0 = source._c0;
        _c1 = source._c1;
        _r0 = source._r0;
        _r1 = source._r1;
        _transform = source._transform;
        _inverse = source._inverse;
        _cdx = source._cdx;
        _cdy = source._cdy;
        _dr = source._dr;
        _a = source._a;
    }

    protected override GradientBrush WithColors(Vector4[] colors) => new ConicalGradientBrush(this, colors);

    protected override void Positions(int x, int y, Span<float> t)
    {
        double py = y + 0.5;
        double r0 = _r0, dr = _dr, a = _a, cdx = _cdx, cdy = _cdy;
        bool concentricEqual = cdx == 0 && cdy == 0 && dr == 0;
        double r0sq = r0 * r0;
        for (int i = 0; i < t.Length; i++)
        {
            _inverse.Map(x + i + 0.5, py, out double u, out double v);
            double pdx = u - _c0.X, pdy = v - _c0.Y;
            double c = (pdx * pdx) + (pdy * pdy) - r0sq;
            if (concentricEqual)
            {
                // Equal circles never sweep an area: the disc itself takes the first color and nothing else is painted.
                t[i] = c <= 0 ? 0f : float.NaN;
                continue;
            }

            double b = (pdx * cdx) + (pdy * cdy) + (r0 * dr);
            double result;
            if (Math.Abs(a) < 1e-12 * ((cdx * cdx) + (cdy * cdy) + (dr * dr)))
            {
                // The circles touch the pixel along a single solution: -2bt + c = 0.
                if (b == 0)
                {
                    t[i] = float.NaN;
                    continue;
                }

                result = c / (2 * b);
                if (!(r0 + (result * dr) >= 0))
                {
                    t[i] = float.NaN;
                    continue;
                }
            }
            else
            {
                double disc = (b * b) - (a * c);
                if (disc < 0)
                {
                    t[i] = float.NaN;
                    continue;
                }

                double sq = Math.Sqrt(disc);
                double t1 = (b + sq) / a, t2 = (b - sq) / a;
                double hi = Math.Max(t1, t2), lo = Math.Min(t1, t2);
                if (r0 + (hi * dr) >= 0)
                {
                    result = hi;
                }
                else if (r0 + (lo * dr) >= 0)
                {
                    result = lo;
                }
                else
                {
                    t[i] = float.NaN;
                    continue;
                }
            }

            t[i] = (float)result;
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [.. StopParameters("focal-radial-gradient"), P("cx", _c1.X), P("cy", _c1.Y), P("radius", _r1), P("fx", _c0.X), P("fy", _c0.Y), P("fr", _r0), .. MatrixLinearGradientBrush.MatrixParameters(_transform)];
}
