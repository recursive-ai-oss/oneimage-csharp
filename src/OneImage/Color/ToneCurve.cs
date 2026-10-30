namespace OneImage;

/// <summary>
/// A one-dimensional tone curve from an ICC profile: either a sampled table (linear interpolation between entries,
/// input clamped to 0..1) or a parametric function. Evaluation is plain scalar float math so every hardware path agrees.
/// </summary>
internal sealed class ToneCurve
{
    private readonly float[]? _table;

    // Parametric form: x >= D ? (A*x + B)^G + E : C*x + F. Covers ICC parametric types 0..4.
    private readonly double _g, _a, _b, _c, _d, _e, _f;

    // When set, evaluates the inverse of the parametric function above.
    private bool _inverse;

    private ToneCurve(float[]? table, double g, double a, double b, double c, double d, double e, double f)
    {
        _table = table;
        _g = g;
        _a = a;
        _b = b;
        _c = c;
        _d = d;
        _e = e;
        _f = f;
    }

    public static ToneCurve Identity { get; } = new(null, 1, 1, 0, 0, double.NegativeInfinity, 0, 0);

    public bool IsIdentity => !_inverse && _table == null && _g == 1 && _a == 1 && _b == 0 && double.IsNegativeInfinity(_d) && _e == 0;

    public static ToneCurve Gamma(double g) => new(null, g, 1, 0, 0, double.NegativeInfinity, 0, 0);

    public static ToneCurve FromTable(float[] table) => table.Length switch
    {
        0 => Identity,
        _ => new ToneCurve(table, 1, 1, 0, 0, 0, 0, 0),
    };

    /// <summary>Builds a parametric curve; <paramref name="p"/> holds the ICC parameters for <paramref name="type"/> in order (g, a, b, c, d, e, f).</summary>
    public static ToneCurve Parametric(int type, ReadOnlySpan<double> p)
    {
        double g = p[0];
        switch (type)
        {
            case 0:
                return Gamma(g);
            case 1:
                return new ToneCurve(null, g, p[1], p[2], 0, p[1] != 0 ? -p[2] / p[1] : 0, 0, 0);
            case 2:
                return new ToneCurve(null, g, p[1], p[2], 0, p[1] != 0 ? -p[2] / p[1] : 0, p[3], p[3]);
            case 3:
                return new ToneCurve(null, g, p[1], p[2], p[3], p[4], 0, 0);
            default:
                return new ToneCurve(null, g, p[1], p[2], p[3], p[4], p[5], p[6]);
        }
    }

    /// <summary>Samples an analytic curve into a 16385-entry table so it evaluates with one interpolation; tables and identities are returned as they are.</summary>
    public ToneCurve Tabulate()
    {
        if (_table != null || IsIdentity)
        {
            return this;
        }

        const int N = 16384;
        var t = new float[N + 1];
        for (int i = 0; i <= N; i++)
        {
            t[i] = Eval((float)i / N);
        }

        return new ToneCurve(t, 1, 1, 0, 0, 0, 0, 0);
    }

    public float Eval(float x)
    {
        if (_table != null)
        {
            if (!(x > 0f))
            {
                return _table[0];
            }

            if (x >= 1f)
            {
                return _table[^1];
            }

            float pos = x * (_table.Length - 1);
            int i = (int)pos;
            float t = pos - i;
            return _table[i] + ((_table[i + 1] - _table[i]) * t);
        }

        if (IsIdentity)
        {
            return x;
        }

        if (_inverse)
        {
            return EvalInverse(x);
        }

        double xd = x;
        double y;
        if (xd >= _d)
        {
            double basev = (_a * xd) + _b;
            y = (basev > 0 ? DeterministicMath.Pow(basev, _g) : 0) + _e;
        }
        else
        {
            y = (_c * xd) + _f;
        }

        return (float)y;
    }

    public void EvalRow(Span<float> data, int stride, int offset, int count)
    {
        for (int i = 0, p = offset; i < count; i++, p += stride)
        {
            data[p] = Eval(data[p]);
        }
    }

    /// <summary>Returns the inverse curve. Sampled tables are inverted numerically into 4096 samples; parametric curves invert analytically.</summary>
    public ToneCurve Inverse()
    {
        if (_table != null)
        {
            return InvertTable(_table);
        }

        if (IsIdentity)
        {
            return this;
        }

        var inv = new ToneCurve(null, _g, _a, _b, _c, _d, _e, _f) { _inverse = !_inverse };
        return inv;
    }

    private float EvalInverse(float y)
    {
        // Inverse of the power segment is x = ((y - e)^(1/g) - b) / a; the linear segment inverts to x = (y - f) / c.
        // The threshold is the value the linear segment reaches at d.
        double yd = double.IsNegativeInfinity(_d) ? double.NegativeInfinity : (_c * _d) + _f;
        double yy = y;
        double x;
        if (yy >= yd && _g != 0 && _a != 0)
        {
            double v = yy - _e;
            x = ((v > 0 ? DeterministicMath.Pow(v, 1.0 / _g) : 0) - _b) / _a;
            double lo = double.IsNegativeInfinity(_d) ? 0 : _d;
            if (x < lo)
            {
                x = lo;
            }
        }
        else if (_c != 0)
        {
            x = (yy - _f) / _c;
        }
        else
        {
            x = double.IsNegativeInfinity(_d) ? 0 : _d;
        }

        return (float)x;
    }

    private static ToneCurve InvertTable(float[] t)
    {
        const int N = 4096;
        int n = t.Length;
        bool increasing = t[^1] >= t[0];
        var inv = new float[N + 1];
        int j = 0;
        for (int i = 0; i <= N; i++)
        {
            float y = (float)i / N;
            // Walk the monotone table to the segment containing y.
            if (increasing)
            {
                while (j < n - 2 && t[j + 1] < y)
                {
                    j++;
                }

                float y0 = t[j], y1 = t[j + 1];
                float f = y1 > y0 ? Math.Clamp((y - y0) / (y1 - y0), 0f, 1f) : (y <= y0 ? 0f : 1f);
                inv[i] = (j + f) / (n - 1);
            }
            else
            {
                while (j < n - 2 && t[j + 1] > y)
                {
                    j++;
                }

                float y0 = t[j], y1 = t[j + 1];
                float f = y0 > y1 ? Math.Clamp((y0 - y) / (y0 - y1), 0f, 1f) : 0f;
                inv[i] = (j + f) / (n - 1);
            }
        }

        return new ToneCurve(inv, 1, 1, 0, 0, 0, 0, 0);
    }
}
