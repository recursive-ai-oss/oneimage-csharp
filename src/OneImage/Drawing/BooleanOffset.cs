namespace OneImage.Drawing;

/// <summary>
/// Builds the pieces whose union is the set of points within a distance of a polygon's boundary: one rectangle per edge and one
/// wedge per corner (a round fan, a bevel triangle, or a mitered kite). The pieces are convex and all wound the same way as the
/// result of a boolean operation, so a non-zero union of them is exactly the offset band.
/// </summary>
internal static class BooleanOffset
{
    public static Path Pieces(Path resolved, double d, StrokeJoin join, double miterLimit, float tolerance)
    {
        var verbs = new List<byte>();
        var coords = new List<float>();
        var buf = new List<(double X, double Y)>(32);
        foreach (Contour c in resolved.Flatten(tolerance))
        {
            System.Numerics.Vector2[] p = c.Points;
            int n = p.Length;
            if (n < 3)
            {
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                System.Numerics.Vector2 u = p[i], v = p[(i + 1) % n], w = p[(i + 2) % n];
                double e1x = v.X - u.X, e1y = v.Y - u.Y, e2x = w.X - v.X, e2y = w.Y - v.Y;
                double l1 = Math.Sqrt((e1x * e1x) + (e1y * e1y)), l2 = Math.Sqrt((e2x * e2x) + (e2y * e2y));
                if (l1 == 0 || l2 == 0)
                {
                    continue;
                }

                e1x /= l1;
                e1y /= l1;
                e2x /= l2;
                e2y /= l2;

                // Left normals on screen (y down): (ey, -ex).
                double n1x = e1y, n1y = -e1x, n2x = e2y, n2y = -e2x;
                buf.Clear();
                buf.Add((u.X + (n1x * d), u.Y + (n1y * d)));
                buf.Add((v.X + (n1x * d), v.Y + (n1y * d)));
                buf.Add((v.X - (n1x * d), v.Y - (n1y * d)));
                buf.Add((u.X - (n1x * d), u.Y - (n1y * d)));
                Emit(verbs, coords, buf);

                // The corner wedge on the outer side of the turn.
                double side = (n1x * e2x) + (n1y * e2y) < 0 ? 1 : -1;
                if (Math.Abs((n1x * e2x) + (n1y * e2y)) < 1e-9)
                {
                    continue;
                }

                double ax = side * n1x, ay = side * n1y, bx = side * n2x, by = side * n2y;
                buf.Clear();
                buf.Add((v.X, v.Y));
                buf.Add((v.X + (ax * d), v.Y + (ay * d)));
                double cos = (ax * bx) + (ay * by);
                switch (join)
                {
                    case StrokeJoin.Round:
                        {
                            double a0 = Math.Atan2(ay, ax);
                            double delta = Math.Atan2((ax * by) - (ay * bx), cos);
                            double step = d <= tolerance ? Math.PI / 2 : Math.Max(2 * Math.Acos(1 - (tolerance / d)), 0.02);
                            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / step));
                            for (int k = 1; k < steps; k++)
                            {
                                double t = a0 + (delta * k / steps);
                                buf.Add((v.X + (Math.Cos(t) * d), v.Y + (Math.Sin(t) * d)));
                            }

                            break;
                        }

                    case StrokeJoin.Miter:
                        {
                            // Tip distance from the corner is d / cos(turn / 2); beyond the limit the corner is cut like a bevel.
                            double ratio = Math.Sqrt(2 / (1 + cos));
                            if (1 + cos > 1e-9 && ratio <= miterLimit)
                            {
                                double k = d / (1 + cos);
                                buf.Add((v.X + ((ax + bx) * k), v.Y + ((ay + by) * k)));
                            }

                            break;
                        }

                    case StrokeJoin.Square:
                        {
                            // Cut flat at distance d from the corner along the bisector (Clipper's square join).
                            double mx = ax + bx, my = ay + by, ml = Math.Sqrt((mx * mx) + (my * my));
                            if (ml > 1e-9)
                            {
                                mx /= ml;
                                my /= ml;
                                double along1 = (mx * e1x) + (my * e1y);
                                double along2 = (mx * e2x) + (my * e2y);
                                if (Math.Abs(along1) > 1e-9 && Math.Abs(along2) > 1e-9)
                                {
                                    double t1 = d * (1 - ((mx * ax) + (my * ay))) / along1;
                                    double t2 = d * (1 - ((mx * bx) + (my * by))) / along2;
                                    buf.Add((v.X + (ax * d) + (e1x * t1), v.Y + (ay * d) + (e1y * t1)));
                                    buf.Add((v.X + (bx * d) + (e2x * t2), v.Y + (by * d) + (e2y * t2)));
                                }
                            }

                            break;
                        }
                }

                buf.Add((v.X + (bx * d), v.Y + (by * d)));
                Emit(verbs, coords, buf);
            }
        }

        return verbs.Count == 0 ? Path.Empty : new Path([.. verbs], [.. coords]);
    }

    // Writes one convex polygon, wound like boolean results (negative signed area on screen coordinates).
    private static void Emit(List<byte> verbs, List<float> coords, List<(double X, double Y)> pts)
    {
        double area = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        if (area == 0)
        {
            return;
        }

        verbs.Add((byte)Verb.Move);
        for (int i = 0; i < pts.Count; i++)
        {
            var q = area < 0 ? pts[i] : pts[pts.Count - 1 - i];
            if (i > 0)
            {
                verbs.Add((byte)Verb.Line);
            }

            coords.Add((float)q.X);
            coords.Add((float)q.Y);
        }

        verbs.Add((byte)Verb.Close);
    }
}
