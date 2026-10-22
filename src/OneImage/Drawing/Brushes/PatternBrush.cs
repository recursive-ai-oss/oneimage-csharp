using System.Numerics;
using System.Text;

namespace OneImage.Drawing;

/// <summary>Tiles a two-color bitmap.</summary>
internal sealed class PatternBrush : Brush
{
    private const int MaxSide = 4096;
    private readonly bool[] _cells;
    private readonly int _w, _h;
    private readonly Rgba32 _fore, _back;
    private readonly Vector4 _f, _b;
    private readonly Matrix3x2? _transform;
    private readonly Matrix3x2 _inverse;

    public PatternBrush(Rgba32 foreground, Rgba32 background, bool[,] pattern, Matrix3x2? transform)
        : this(foreground, background, Flatten(pattern, out int w, out int h), w, h, transform)
    {
    }

    private PatternBrush(Rgba32 foreground, Rgba32 background, bool[] cells, int w, int h, Matrix3x2? transform)
    {
        _fore = foreground;
        _back = background;
        _f = ToVector(foreground);
        _b = ToVector(background);
        _cells = cells;
        _w = w;
        _h = h;
        _transform = transform;
        if (transform is { } m)
        {
            if (!float.IsFinite(m.M11 + m.M12 + m.M21 + m.M22 + m.M31 + m.M32) || !Matrix3x2.Invert(m, out _inverse))
            {
                throw new ArgumentException("The transform must be finite and invertible.", nameof(transform));
            }
        }
    }

    private PatternBrush(PatternBrush source, Vector4 f, Vector4 b)
    {
        _fore = source._fore;
        _back = source._back;
        _cells = source._cells;
        _w = source._w;
        _h = source._h;
        _transform = source._transform;
        _inverse = source._inverse;
        _f = f;
        _b = b;
    }

    private static Vector4 ToVector(Rgba32 c) => new Vector4(c.R, c.G, c.B, c.A) * (1f / 255f);

    private static bool[] Flatten(bool[,] pattern, out int w, out int h)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        h = pattern.GetLength(0);
        w = pattern.GetLength(1);
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide)
        {
            throw new ArgumentException($"A pattern is 1 to {MaxSide} cells in each direction.", nameof(pattern));
        }

        var cells = new bool[w * h];
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                cells[(r * w) + c] = pattern[r, c];
            }
        }

        return cells;
    }

    /// <summary>The 4×4 cells of a preset hatch, as probed from how the hatches look in ImageSharp.Drawing (anchored at the image origin).</summary>
    public static bool[,] Preset(HatchStyle style)
    {
        if (style is < HatchStyle.Horizontal or > HatchStyle.Min)
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }

        var p = new bool[4, 4];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                p[r, c] = style switch
                {
                    HatchStyle.Horizontal => r == 1,
                    HatchStyle.Vertical => c == 1,
                    HatchStyle.ForwardDiagonal => (r + c) % 4 == 3,
                    HatchStyle.BackwardDiagonal => r == c,
                    HatchStyle.Percent10 => (r == 0 && c == 0) || (r == 2 && c == 2),
                    HatchStyle.Percent20 => (r % 2 == 0 && c == 0) || (r % 2 == 1 && c == 2),
                    _ => r == 3,
                };
            }
        }

        return p;
    }

    internal override Brush Linearized(ColorMap map) => new PatternBrush(this, map.Map(_f), map.Map(_b));

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int n = rgba.Length / 4;
        if (_transform == null)
        {
            int row = (int)Mod(y, _h) * _w;
            int col = (int)Mod(x, _w);
            for (int i = 0; i < n; i++)
            {
                Write(rgba, i, _cells[row + col]);
                if (++col == _w)
                {
                    col = 0;
                }
            }

            return;
        }

        Matrix3x2 m = _inverse;
        double py = y + 0.5;
        for (int i = 0; i < n; i++)
        {
            double px = x + i + 0.5;
            double u = (px * m.M11) + (py * m.M21) + m.M31;
            double v = (px * m.M12) + (py * m.M22) + m.M32;
            if (!(Math.Abs(u) < 1e15 && Math.Abs(v) < 1e15))
            {
                Write(rgba, i, false);
                continue;
            }

            long cu = (long)Math.Floor(u), cv = (long)Math.Floor(v);
            Write(rgba, i, _cells[(int)(Mod(cv, _h) * _w) + (int)Mod(cu, _w)]);
        }
    }

    private void Write(Span<float> rgba, int i, bool on)
    {
        Vector4 c = on ? _f : _b;
        int o = i * 4;
        rgba[o] = c.X;
        rgba[o + 1] = c.Y;
        rgba[o + 2] = c.Z;
        rgba[o + 3] = c.W;
    }

    private static long Mod(long a, int m)
    {
        long r = a % m;
        return r < 0 ? r + m : r;
    }

    public override IReadOnlyList<OpParameter> Describe()
    {
        var bits = new StringBuilder(_cells.Length);
        foreach (bool c in _cells)
        {
            bits.Append(c ? '1' : '0');
        }

        var list = new List<OpParameter>
        {
            new("brush", "pattern"),
            new("foreground", Hex(_fore)),
            new("background", Hex(_back)),
            new("width", (long)_w),
            new("height", (long)_h),
            new("cells", bits.ToString()),
        };
        if (_transform is { } m)
        {
            list.Add(new("m11", (double)m.M11));
            list.Add(new("m12", (double)m.M12));
            list.Add(new("m21", (double)m.M21));
            list.Add(new("m22", (double)m.M22));
            list.Add(new("m31", (double)m.M31));
            list.Add(new("m32", (double)m.M32));
        }

        return list;
    }
}
