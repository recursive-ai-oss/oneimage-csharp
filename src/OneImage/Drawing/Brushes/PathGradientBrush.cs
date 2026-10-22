using System.Globalization;
using System.Numerics;

namespace OneImage.Drawing;

/// <summary>Blends vertex colors toward a center color across the triangles formed by the center and each edge.</summary>
internal sealed class PathGradientBrush : Brush
{
    private const int MaxPoints = 1 << 16;
    private const float Eps = 1e-5f;
    private readonly Vector2[] _points;
    private readonly Vector4[] _colors;
    private readonly Vector2 _center;
    private readonly Vector4 _centerColor;
    private readonly Rgba32[] _originalColors;
    private readonly Rgba32? _originalCenterColor;
    private readonly bool _customCenter;

    // Per triangle (center, p[i], p[i+1]): the inverse of the edge matrix, so a point's weights come from two multiplies.
    private readonly Tri[] _tris;
    private readonly float _minX, _minY, _maxX, _maxY;

    public PathGradientBrush(ReadOnlySpan<Vector2> points, ReadOnlySpan<Rgba32> colors, Rgba32? centerColor, Vector2? center)
    {
        if (points.Length < 3 || points.Length > MaxPoints)
        {
            throw new ArgumentException($"A path gradient needs 3 to {MaxPoints} points.", nameof(points));
        }

        if (colors.Length != points.Length)
        {
            throw new ArgumentException("A path gradient needs one color per point.", nameof(colors));
        }

        Vector2 sum = default;
        Vector4 colorSum = default;
        _points = points.ToArray();
        _originalColors = colors.ToArray();
        _colors = new Vector4[colors.Length];
        for (int i = 0; i < _points.Length; i++)
        {
            if (!float.IsFinite(_points[i].X) || !float.IsFinite(_points[i].Y))
            {
                throw new ArgumentException("Points must be finite.", nameof(points));
            }

            sum += _points[i];
            Rgba32 c = colors[i];
            _colors[i] = new Vector4(c.R, c.G, c.B, c.A) * (1f / 255f);
            colorSum += _colors[i];
        }

        if (center is { } given)
        {
            if (!float.IsFinite(given.X) || !float.IsFinite(given.Y))
            {
                throw new ArgumentException("The center must be finite.", nameof(center));
            }

            _center = given;
            _customCenter = true;
        }
        else
        {
            _center = sum / _points.Length;
        }

        _originalCenterColor = centerColor;
        _centerColor = centerColor is { } cc ? new Vector4(cc.R, cc.G, cc.B, cc.A) * (1f / 255f) : colorSum / _points.Length;
        (_tris, _minX, _minY, _maxX, _maxY) = Build(_points, _center);
    }

    private PathGradientBrush(PathGradientBrush source, Vector4[] colors, Vector4 centerColor)
    {
        _points = source._points;
        _originalColors = source._originalColors;
        _originalCenterColor = source._originalCenterColor;
        _customCenter = source._customCenter;
        _center = source._center;
        _colors = colors;
        _centerColor = centerColor;
        _tris = source._tris;
        _minX = source._minX;
        _minY = source._minY;
        _maxX = source._maxX;
        _maxY = source._maxY;
    }

    private readonly record struct Tri(float Cx, float Cy, float IA, float IB, float IC, float ID, int V0, int V1);

    private static (Tri[] Tris, float MinX, float MinY, float MaxX, float MaxY) Build(Vector2[] p, Vector2 c)
    {
        var tris = new List<Tri>(p.Length);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < p.Length; i++)
        {
            int j = (i + 1) % p.Length;
            Vector2 e1 = p[i] - c, e2 = p[j] - c;
            double det = ((double)e1.X * e2.Y) - ((double)e1.Y * e2.X);
            minX = MathF.Min(minX, p[i].X);
            minY = MathF.Min(minY, p[i].Y);
            maxX = MathF.Max(maxX, p[i].X);
            maxY = MathF.Max(maxY, p[i].Y);
            if (Math.Abs(det) < 1e-12)
            {
                continue; // a sliver with no area
            }

            // q = s*e1 + u*e2  =>  s = (q.x*e2.y - q.y*e2.x)/det, u = (e1.x*q.y - e1.y*q.x)/det
            tris.Add(new Tri(c.X, c.Y, (float)(e2.Y / det), (float)(-e2.X / det), (float)(-e1.Y / det), (float)(e1.X / det), i, j));
        }

        return (tris.ToArray(), minX, minY, maxX, maxY);
    }

    internal override Brush Linearized(ColorMap map)
    {
        var mapped = new Vector4[_colors.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            mapped[i] = map.Map(_colors[i]);
        }

        return new PathGradientBrush(this, mapped, map.Map(_centerColor));
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int n = rgba.Length / 4;
        float py = y + 0.5f;
        rgba[..(n * 4)].Clear();
        if (py < _minY - Eps || py > _maxY + Eps)
        {
            return;
        }

        Tri[] tris = _tris;
        for (int i = 0; i < n; i++)
        {
            float px = x + i + 0.5f;
            if (px < _minX - Eps || px > _maxX + Eps)
            {
                continue;
            }

            for (int k = 0; k < tris.Length; k++)
            {
                ref readonly Tri t = ref tris[k];
                float qx = px - t.Cx, qy = py - t.Cy;
                float s = (qx * t.IA) + (qy * t.IB);
                float u = (qx * t.IC) + (qy * t.ID);
                if (s >= -Eps && u >= -Eps && s + u <= 1f + Eps)
                {
                    s = Math.Clamp(s, 0f, 1f);
                    u = Math.Clamp(u, 0f, 1f - s);
                    Vector4 c = (_centerColor * (1f - s - u)) + (_colors[t.V0] * s) + (_colors[t.V1] * u);
                    int o = i * 4;
                    rgba[o] = c.X;
                    rgba[o + 1] = c.Y;
                    rgba[o + 2] = c.Z;
                    rgba[o + 3] = c.W;
                    break;
                }
            }
        }
    }

    public override IReadOnlyList<OpParameter> Describe()
    {
        var pts = new System.Text.StringBuilder();
        var cols = new System.Text.StringBuilder();
        for (int i = 0; i < _points.Length; i++)
        {
            if (i > 0)
            {
                pts.Append(';');
                cols.Append(';');
            }

            pts.Append(_points[i].X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(_points[i].Y.ToString("R", CultureInfo.InvariantCulture));
            cols.Append(Hex(_originalColors[i]));
        }

        var list = new List<OpParameter>
        {
            new("brush", "path-gradient"),
            new("points", pts.ToString()),
            new("colors", cols.ToString()),
        };
        if (_originalCenterColor is { } cc)
        {
            list.Add(new("center-color", Hex(cc)));
        }

        if (_customCenter)
        {
            list.Add(new("cx", (double)_center.X));
            list.Add(new("cy", (double)_center.Y));
        }

        return list;
    }
}
