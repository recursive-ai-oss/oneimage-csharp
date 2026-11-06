using System.Numerics;

namespace OneImage.Metafile;

/// <summary>A 2-D affine transform in double precision, row-vector convention: <c>x' = x*M11 + y*M21 + Dx</c>.</summary>
internal readonly struct Mat
{
    public readonly double M11, M12, M21, M22, Dx, Dy;

    public Mat(double m11, double m12, double m21, double m22, double dx, double dy)
    {
        M11 = m11;
        M12 = m12;
        M21 = m21;
        M22 = m22;
        Dx = dx;
        Dy = dy;
    }

    public static Mat Identity => new(1, 0, 0, 1, 0, 0);

    public static Mat Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Mat Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>Returns the transform that applies this one first and <paramref name="o"/> second.</summary>
    public Mat Then(Mat o) => new(
        (M11 * o.M11) + (M12 * o.M21),
        (M11 * o.M12) + (M12 * o.M22),
        (M21 * o.M11) + (M22 * o.M21),
        (M21 * o.M12) + (M22 * o.M22),
        (Dx * o.M11) + (Dy * o.M21) + o.Dx,
        (Dx * o.M12) + (Dy * o.M22) + o.Dy);

    public (double X, double Y) Apply(double x, double y) => ((x * M11) + (y * M21) + Dx, (x * M12) + (y * M22) + Dy);

    public Vector2 ApplyV(double x, double y)
    {
        (double a, double b) = Apply(x, y);
        return new Vector2((float)a, (float)b);
    }

    public double Determinant => (M11 * M22) - (M12 * M21);

    public bool IsInvertible => Math.Abs(Determinant) > 1e-12 && double.IsFinite(Determinant);

    public bool IsAxisAligned => Math.Abs(M12) < 1e-9 * (Math.Abs(M11) + 1e-30) && Math.Abs(M21) < 1e-9 * (Math.Abs(M22) + 1e-30);

    /// <summary>Applies only the linear part of the inverse to a vector (no translation).</summary>
    public (double X, double Y) InverseVector(double x, double y)
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-12)
        {
            return (0, 0);
        }

        return (((x * M22) - (y * M21)) / det, ((-x * M12) + (y * M11)) / det);
    }

    /// <summary>Gets the geometric mean scale factor, the square root of the absolute determinant.</summary>
    public double MeanScale => Math.Sqrt(Math.Abs(Determinant));

    public Matrix3x2 ToMatrix3x2() => new((float)M11, (float)M12, (float)M21, (float)M22, (float)Dx, (float)Dy);

    public bool Equals(Mat o) => M11 == o.M11 && M12 == o.M12 && M21 == o.M21 && M22 == o.M22 && Dx == o.Dx && Dy == o.Dy;
}

/// <summary>Arc and ellipse geometry built from cubic Béziers.</summary>
internal static class ArcGeometry
{
    /// <summary>
    /// Appends an elliptical arc from parametric angle <paramref name="start"/> through <paramref name="sweep"/> (radians; angles grow
    /// from +x toward +y) of the axis-aligned ellipse centered on (cx, cy). The first point is added with a line (or a move when the
    /// builder has no current point), not as a new figure when <paramref name="connect"/> is true.
    /// </summary>
    public static void AddArc(MetafilePathBuilder b, double cx, double cy, double rx, double ry, double start, double sweep, bool connect)
    {
        double sx = cx + (rx * Math.Cos(start));
        double sy = cy + (ry * Math.Sin(start));
        var p0 = new Vector2((float)sx, (float)sy);
        if (connect && b.HasCurrentPoint)
        {
            b.LineTo(p0);
        }
        else
        {
            b.MoveTo(p0);
        }

        int n = (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9);
        n = Math.Clamp(n, 1, 64); // a sweep is at most a few turns; the clamp also covers NaN and overflow of the cast
        double step = sweep / n;
        double k = 4.0 / 3.0 * Math.Tan(step / 4);
        double a = start;
        for (int i = 0; i < n; i++)
        {
            double a1 = a + step;
            double c0 = Math.Cos(a), s0 = Math.Sin(a), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
            var q1 = new Vector2((float)(cx + (rx * (c0 - (k * s0)))), (float)(cy + (ry * (s0 + (k * c0)))));
            var q2 = new Vector2((float)(cx + (rx * (c1 + (k * s1)))), (float)(cy + (ry * (s1 - (k * c1)))));
            var q3 = new Vector2((float)(cx + (rx * c1)), (float)(cy + (ry * s1)));
            b.CubicTo(q1, q2, q3);
            a = a1;
        }
    }

    /// <summary>Appends a closed ellipse inscribed in the box.</summary>
    public static void AddEllipse(MetafilePathBuilder b, double l, double t, double r, double bt)
    {
        double cx = (l + r) / 2, cy = (t + bt) / 2, rx = Math.Abs(r - l) / 2, ry = Math.Abs(bt - t) / 2;
        AddArc(b, cx, cy, rx, ry, 0, Math.PI * 2, false);
        b.Close();
    }

    /// <summary>Appends a closed rectangle with elliptical corners; <paramref name="cw"/> and <paramref name="ch"/> are the corner ellipse's width and height.</summary>
    public static void AddRoundRect(MetafilePathBuilder b, double l, double t, double r, double bt, double cw, double ch)
    {
        double rx = Math.Min(Math.Abs(cw) / 2, Math.Abs(r - l) / 2);
        double ry = Math.Min(Math.Abs(ch) / 2, Math.Abs(bt - t) / 2);
        if (rx < 1e-6 || ry < 1e-6)
        {
            b.AddRectangle((float)l, (float)t, (float)r, (float)bt);
            return;
        }

        double k = 0.5522847498307936;
        b.MoveTo(new Vector2((float)(l + rx), (float)t));
        b.LineTo(new Vector2((float)(r - rx), (float)t));
        b.CubicTo(new Vector2((float)(r - rx + (k * rx)), (float)t), new Vector2((float)r, (float)(t + ry - (k * ry))), new Vector2((float)r, (float)(t + ry)));
        b.LineTo(new Vector2((float)r, (float)(bt - ry)));
        b.CubicTo(new Vector2((float)r, (float)(bt - ry + (k * ry))), new Vector2((float)(r - rx + (k * rx)), (float)bt), new Vector2((float)(r - rx), (float)bt));
        b.LineTo(new Vector2((float)(l + rx), (float)bt));
        b.CubicTo(new Vector2((float)(l + rx - (k * rx)), (float)bt), new Vector2((float)l, (float)(bt - ry + (k * ry))), new Vector2((float)l, (float)(bt - ry)));
        b.LineTo(new Vector2((float)l, (float)(t + ry)));
        b.CubicTo(new Vector2((float)l, (float)(t + ry - (k * ry))), new Vector2((float)(l + rx - (k * rx)), (float)t), new Vector2((float)(l + rx), (float)t));
        b.Close();
    }

    /// <summary>
    /// The parametric angle of the ray from the center through (x, y), on the axis-aligned ellipse with radii rx and ry, as GDI derives the
    /// end points of Arc, Chord and Pie.
    /// </summary>
    public static double RayAngle(double cx, double cy, double rx, double ry, double x, double y)
    {
        double dx = rx == 0 ? 0 : (x - cx) / rx;
        double dy = ry == 0 ? 0 : (y - cy) / ry;
        return Math.Atan2(dy, dx);
    }

    /// <summary>
    /// Returns the sweep from <paramref name="a1"/> to <paramref name="a2"/> in the drawing direction. <paramref name="visualCounterclockwise"/>
    /// is the direction on the device; in logical coordinates with y downward that is decreasing angle, and <paramref name="flipped"/>
    /// reverses it when the logical-to-device transform mirrors.
    /// </summary>
    public static double Sweep(double a1, double a2, bool visualCounterclockwise, bool flipped)
    {
        bool decreasing = visualCounterclockwise != flipped;
        double s = a2 - a1;
        const double TwoPi = Math.PI * 2;
        if (decreasing)
        {
            while (s > 0)
            {
                s -= TwoPi;
            }

            while (s <= -TwoPi)
            {
                s += TwoPi;
            }

            if (Math.Abs(s) < 1e-12)
            {
                s = -TwoPi;
            }
        }
        else
        {
            while (s < 0)
            {
                s += TwoPi;
            }

            while (s >= TwoPi)
            {
                s -= TwoPi;
            }

            if (Math.Abs(s) < 1e-12)
            {
                s = TwoPi;
            }
        }

        return s;
    }
}
