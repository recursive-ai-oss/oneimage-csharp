using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using OneImage.Text;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

/// <summary>One element of a text subtree (the <c>text</c> element itself, a <c>tspan</c>, a <c>textPath</c>, an <c>a</c> or a <c>tref</c>) with the style its characters share.</summary>
internal sealed class TSpan
{
    public int Id { get; init; }

    public required SvgElement El { get; init; }

    public required SvgStyle St { get; init; }

    public required TextStyle Ts { get; init; }

    public TSpan? Parent { get; init; }

    /// <summary>The first character (UTF-16 index into the text) and one past the last.</summary>
    public int Start { get; set; }

    public int End { get; set; }

    /// <summary>The nearest <c>textPath</c> span at or above this one, or <see langword="null"/>.</summary>
    public TSpan? PathSpan { get; set; }

    public PathRoute? Route { get; set; }

    public double StartOffset { get; set; }

    public bool StartOffsetPercent { get; set; }

    /// <summary>The <c>textLength</c> in pixels, NaN when none; whether <c>lengthAdjust</c> stretches the glyphs.</summary>
    public double TextLength { get; set; } = double.NaN;

    public bool StretchGlyphs { get; set; }

    public FontChain? Chain { get; set; }

    /// <summary>The font that sets the span's baselines and decorations.</summary>
    public Font? Primary { get; set; }

    /// <summary>How far the span's baseline is below the text element's <c>y</c> (pixels, down is positive), from <c>dominant-baseline</c>, <c>alignment-baseline</c> and <c>baseline-shift</c>.</summary>
    public double ShiftDown { get; set; }

    public bool ResolvedPaints { get; set; }

    public ResolvedPaint Fill { get; set; }

    public ResolvedPaint Stroke { get; set; }

    public bool IsDescendantOrSelfOf(TSpan other)
    {
        for (TSpan? s = this; s is not null; s = s.Parent)
        {
            if (ReferenceEquals(s, other))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Everything the layout of one <c>text</c> element needs: its characters after white-space processing, who owns each one, and their coordinate lists.</summary>
internal sealed class TextModel
{
    public readonly System.Text.StringBuilder Text = new();
    public readonly List<TSpan> Spans = [];
    public int[] CharSpan = new int[64];
    public double[]? X;
    public double[]? Y;
    public double[]? Dx;
    public double[]? Dy;
    public double[]? Rot;

    public int Length => Text.Length;

    public void Append(char c, int span)
    {
        int n = Text.Length;
        if (n == CharSpan.Length)
        {
            Array.Resize(ref CharSpan, n * 2);
        }

        CharSpan[n] = span;
        Text.Append(c);
    }

    public static bool IsSet(double[]? a, int i) => a is not null && i < a.Length && !double.IsNaN(a[i]);

    public static double Get(double[]? a, int i) => a is not null && i < a.Length && !double.IsNaN(a[i]) ? a[i] : double.NaN;

    /// <summary>Sets <paramref name="a"/>[i] when no nearer element has set it (a nested element's value wins over its parent's).</summary>
    public static void SetIfUnset(ref double[]? a, int i, double value)
    {
        if (a is null)
        {
            a = new double[Math.Max(i + 1, 16)];
            Array.Fill(a, double.NaN);
        }
        else if (i >= a.Length)
        {
            int old = a.Length;
            Array.Resize(ref a, Math.Max(i + 1, old * 2));
            Array.Fill(a, double.NaN, old, a.Length - old);
        }

        if (double.IsNaN(a[i]))
        {
            a[i] = value;
        }
    }
}

/// <summary>A cluster with the place it has been given.</summary>
internal sealed class Placed
{
    public required ShapedCluster Cluster { get; init; }

    public required TSpan Span { get; init; }

    public required Font Font { get; init; }

    /// <summary>The first character's index in the model, and how many characters the cluster covers.</summary>
    public int Index { get; init; }

    public int Length { get; init; }

    public byte Level { get; init; }

    /// <summary>The text chunk the cluster belongs to (clusters of one chunk are positioned together).</summary>
    public int Chunk { get; init; }

    /// <summary>The advance in pixels with letter and word spacing and <c>textLength</c> adjustments.</summary>
    public double Advance { get; set; }

    public double XScale { get; set; } = 1;

    /// <summary>Maps the cluster's own space (pen at the origin, y down) to the text element's user space.</summary>
    public Affine Matrix { get; set; }

    public bool Skip { get; set; }

    /// <summary>In vertical text: the cluster stands upright (it is drawn from its upright outline and advances by its vertical advance) instead of lying rotated a quarter turn.</summary>
    public bool Upright { get; init; }

    /// <summary>The cluster's line frame (the matrix of the same cluster set as rotated text on the baseline), which decorations and the line box use; null when it is <see cref="Matrix"/>.</summary>
    public Affine? Frame { get; set; }

    public Affine FrameMatrix => Frame ?? Matrix;

    /// <summary>The outline of the cluster in the space <see cref="Matrix"/> maps from.</summary>
    public Path Outline => Upright ? Cluster.GetUprightOutline() : Cluster.GetOutline();
}

/// <summary>A flattened path with the point and direction at any distance along it. The figures of the path are one after the other.</summary>
internal sealed class PathRoute
{
    private readonly double[] _ax;
    private readonly double[] _ay;
    private readonly double[] _bx;
    private readonly double[] _by;
    private readonly double[] _start;

    private PathRoute(double[] ax, double[] ay, double[] bx, double[] by, double[] start, double total)
    {
        _ax = ax;
        _ay = ay;
        _bx = bx;
        _by = by;
        _start = start;
        Length = total;
    }

    public double Length { get; }

    public bool IsClosed { get; init; }

    public static PathRoute? Create(Path path, bool reverse)
    {
        var ax = new List<double>();
        var ay = new List<double>();
        var bx = new List<double>();
        var by = new List<double>();
        var start = new List<double>();
        double total = 0;
        bool closed = false;
        IReadOnlyList<Contour> contours = path.Flatten(0.01f);
        for (int ci = 0; ci < contours.Count; ci++)
        {
            Contour c = contours[reverse ? contours.Count - 1 - ci : ci];
            Vector2[] pts = c.Points;
            int count = pts.Length;
            for (int k = 0; k < (c.IsClosed ? count : count - 1); k++)
            {
                Vector2 a = pts[reverse ? count - 1 - k : k];
                Vector2 b = pts[reverse ? count - 1 - ((k + 1) % count) : (k + 1) % count];
                double len = Math.Sqrt(((double)(b.X - a.X) * (b.X - a.X)) + ((double)(b.Y - a.Y) * (b.Y - a.Y)));
                if (!(len > 1e-9) || !double.IsFinite(len))
                {
                    continue;
                }

                ax.Add(a.X);
                ay.Add(a.Y);
                bx.Add(b.X);
                by.Add(b.Y);
                start.Add(total);
                total += len;
            }

            closed = contours.Count == 1 && c.IsClosed;
        }

        return ax.Count == 0 ? null : new PathRoute([.. ax], [.. ay], [.. bx], [.. by], [.. start], total) { IsClosed = closed };
    }

    /// <summary>The point and the direction (radians) at a distance along the path; the ends continue straight.</summary>
    public (double X, double Y, double Angle) At(double distance)
    {
        int lo = 0, hi = _start.Length - 1;
        double d = distance;
        if (d <= 0)
        {
            hi = lo = 0;
        }
        else if (d >= Length)
        {
            lo = hi = _start.Length - 1;
        }
        else
        {
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_start[mid] <= d)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
        }

        double dx = _bx[lo] - _ax[lo];
        double dy = _by[lo] - _ay[lo];
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        double t = (d - _start[lo]) / len;
        return (_ax[lo] + (dx * t), _ay[lo] + (dy * t), Math.Atan2(dy, dx));
    }
}
