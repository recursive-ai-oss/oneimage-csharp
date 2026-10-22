namespace OneImage.Text.Fonts;

/// <summary>A glyph outline in font units, y up: figures made of line, quadratic and cubic segments.</summary>
internal sealed class Outline
{
    public const byte Move = 0;
    public const byte Line = 1;
    public const byte Quad = 2;
    public const byte Cubic = 3;
    public const byte Close = 4;

    public static readonly Outline Empty = new([], []);

    public Outline(byte[] verbs, float[] coords)
    {
        Verbs = verbs;
        Coords = coords;
    }

    public byte[] Verbs { get; }

    public float[] Coords { get; }

    public bool IsEmpty => Verbs.Length == 0;

    private (float, float, float, float)? _tight;

    /// <summary>
    /// The exact extents of the curves (not of their control points), in font units. A flat curve whose control points lie outside
    /// the curve itself does not inflate the box. Empty outlines give zeros.
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) TightBox()
    {
        if (_tight is { } cached)
        {
            return cached;
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Add(float x, float y)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        int c = 0;
        float cx = 0, cy = 0;
        for (int v = 0; v < Verbs.Length; v++)
        {
            switch (Verbs[v])
            {
                case Move:
                case Line:
                    cx = Coords[c];
                    cy = Coords[c + 1];
                    Add(cx, cy);
                    c += 2;
                    break;
                case Quad:
                {
                    float x1 = Coords[c], y1 = Coords[c + 1], x2 = Coords[c + 2], y2 = Coords[c + 3];
                    Add(x2, y2);
                    QuadExtremum(cx, x1, x2, cy, y1, y2, Add);
                    QuadExtremum(cy, y1, y2, cx, x1, x2, (b, a) => Add(a, b));
                    cx = x2;
                    cy = y2;
                    c += 4;
                    break;
                }

                case Cubic:
                {
                    float x1 = Coords[c], y1 = Coords[c + 1], x2 = Coords[c + 2], y2 = Coords[c + 3], x3 = Coords[c + 4], y3 = Coords[c + 5];
                    Add(x3, y3);
                    CubicExtrema(cx, x1, x2, x3, cy, y1, y2, y3, Add);
                    CubicExtrema(cy, y1, y2, y3, cx, x1, x2, x3, (b, a) => Add(a, b));
                    cx = x3;
                    cy = y3;
                    c += 6;
                    break;
                }
            }
        }

        var r = minX <= maxX ? (minX, minY, maxX, maxY) : default;
        _tight = r;
        return r;
    }

    // Where the quadratic's derivative along one axis vanishes, the other axis is evaluated at the same t.
    private static void QuadExtremum(float a0, float a1, float a2, float b0, float b1, float b2, Action<float, float> add)
    {
        float d = a0 - (2 * a1) + a2;
        if (d == 0)
        {
            return;
        }

        float t = (a0 - a1) / d;
        if (t > 0 && t < 1)
        {
            float u = 1 - t;
            add((u * u * a0) + (2 * u * t * a1) + (t * t * a2), (u * u * b0) + (2 * u * t * b1) + (t * t * b2));
        }
    }

    private static void CubicExtrema(float a0, float a1, float a2, float a3, float b0, float b1, float b2, float b3, Action<float, float> add)
    {
        // Derivative: 3[(a1-a0)u^2 + 2(a2-a1)ut + (a3-a2)t^2] with u = 1-t, as a quadratic in t.
        double qa = -a0 + (3.0 * a1) - (3.0 * a2) + a3;
        double qb = 2.0 * (a0 - (2.0 * a1) + a2);
        double qc = a1 - a0;
        Span<double> roots = stackalloc double[2];
        int n = 0;
        if (Math.Abs(qa) < 1e-12)
        {
            if (qb != 0)
            {
                roots[n++] = -qc / qb;
            }
        }
        else
        {
            double disc = (qb * qb) - (4 * qa * qc);
            if (disc >= 0)
            {
                double s = Math.Sqrt(disc);
                roots[n++] = (-qb + s) / (2 * qa);
                roots[n++] = (-qb - s) / (2 * qa);
            }
        }

        for (int i = 0; i < n; i++)
        {
            double t = roots[i];
            if (t > 0 && t < 1)
            {
                double u = 1 - t;
                double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
                add((float)((w0 * a0) + (w1 * a1) + (w2 * a2) + (w3 * a3)), (float)((w0 * b0) + (w1 * b1) + (w2 * b2) + (w3 * b3)));
            }
        }
    }

    /// <summary>The box around all points, control points included. Empty outlines give zeros.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) ControlBox()
    {
        if (Coords.Length == 0)
        {
            return default;
        }

        float minX = Coords[0], maxX = Coords[0], minY = Coords[1], maxY = Coords[1];
        for (int i = 2; i + 1 < Coords.Length; i += 2)
        {
            minX = Math.Min(minX, Coords[i]);
            maxX = Math.Max(maxX, Coords[i]);
            minY = Math.Min(minY, Coords[i + 1]);
            maxY = Math.Max(maxY, Coords[i + 1]);
        }

        return (minX, minY, maxX, maxY);
    }
}

/// <summary>Collects outline segments with a hard cap, so a hostile font cannot grow it without bound.</summary>
internal sealed class OutlineBuilder
{
    public const int MaxVerbs = 1 << 18;

    private byte[] _verbs = new byte[32];
    private float[] _coords = new float[96];
    private int _v;
    private int _c;
    private bool _open;

    public bool IsOpen => _open;

    public void MoveTo(float x, float y)
    {
        if (_open)
        {
            Close();
        }

        Add(Outline.Move, 2);
        _coords[_c++] = x;
        _coords[_c++] = y;
        _open = true;
    }

    public void LineTo(float x, float y)
    {
        Add(Outline.Line, 2);
        _coords[_c++] = x;
        _coords[_c++] = y;
    }

    public void QuadTo(float cx, float cy, float x, float y)
    {
        Add(Outline.Quad, 4);
        _coords[_c++] = cx;
        _coords[_c++] = cy;
        _coords[_c++] = x;
        _coords[_c++] = y;
    }

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        Add(Outline.Cubic, 6);
        _coords[_c++] = c1x;
        _coords[_c++] = c1y;
        _coords[_c++] = c2x;
        _coords[_c++] = c2y;
        _coords[_c++] = x;
        _coords[_c++] = y;
    }

    public void Close()
    {
        if (_open)
        {
            Add(Outline.Close, 0);
            _open = false;
        }
    }

    public Outline Build()
    {
        Close();
        for (int i = 0; i < _c; i++)
        {
            // Arithmetic in a charstring can overflow; refuse the glyph rather than hand infinities to the drawing layer.
            if (!float.IsFinite(_coords[i]) || Math.Abs(_coords[i]) > 1e7f)
            {
                throw new FontFormatException("A glyph outline has coordinates out of range.");
            }
        }

        return _v == 0 ? Outline.Empty : new Outline(_verbs.AsSpan(0, _v).ToArray(), _coords.AsSpan(0, _c).ToArray());
    }

    private void Add(byte verb, int coords)
    {
        if (_v >= MaxVerbs)
        {
            throw new FontFormatException("A glyph outline has too many segments.");
        }

        if (_v == _verbs.Length)
        {
            Array.Resize(ref _verbs, _verbs.Length * 2);
        }

        if (_c + coords > _coords.Length)
        {
            Array.Resize(ref _coords, Math.Max(_coords.Length * 2, _c + coords));
        }

        _verbs[_v++] = verb;
    }
}
