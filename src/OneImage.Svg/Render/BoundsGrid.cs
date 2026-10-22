namespace OneImage.Svg.Render;

/// <summary>
/// A uniform grid over the scene that lists, per cell, the top-level nodes whose boxes reach it. A region query then looks at the
/// few cells it covers instead of every node. Nodes that cover many cells are kept in a short list that every query checks.
/// </summary>
internal sealed class BoundsGrid
{
    public const int MinNodes = 128;

    private const int MaxCellsPerAxis = 64;
    private const int MaxCellsPerNode = 16;

    private readonly RNode[] _nodes;
    private readonly int _columns;
    private readonly int _rows;
    private readonly double _cellWidth;
    private readonly double _cellHeight;
    private readonly int[][] _cells;
    private readonly int[] _large;

    public BoundsGrid(RNode[] nodes, double width, double height)
    {
        _nodes = nodes;
        _columns = MaxCellsPerAxis;
        _rows = MaxCellsPerAxis;
        _cellWidth = Math.Max(width, 1) / _columns;
        _cellHeight = Math.Max(height, 1) / _rows;
        var lists = new List<int>?[_columns * _rows];
        var large = new List<int>();
        for (int i = 0; i < nodes.Length; i++)
        {
            RNode n = nodes[i];
            (int c0, int c1) = Range(n.X0, n.X1, _cellWidth, _columns);
            (int r0, int r1) = Range(n.Y0, n.Y1, _cellHeight, _rows);
            if (c1 < c0 || r1 < r0)
            {
                continue;
            }

            if ((c1 - c0 + 1) * (r1 - r0 + 1) > MaxCellsPerNode)
            {
                large.Add(i);
                continue;
            }

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    (lists[(r * _columns) + c] ??= []).Add(i);
                }
            }
        }

        _cells = new int[lists.Length][];
        for (int i = 0; i < lists.Length; i++)
        {
            _cells[i] = lists[i]?.ToArray() ?? [];
        }

        _large = [.. large];
    }

    private static (int First, int Last) Range(double a, double b, double cell, int count)
    {
        if (b < 0 || a >= cell * count)
        {
            // Entirely outside the grid's area: it is still drawable (the scene is clipped by the output, not the grid), so give it the nearest cells.
            return a >= cell * count ? (count - 1, count - 1) : (0, 0);
        }

        int first = (int)Math.Clamp(Math.Floor(a / cell), 0, count - 1);
        int last = (int)Math.Clamp(Math.Floor(b / cell), 0, count - 1);
        return (first, last);
    }

    /// <summary>
    /// Appends to <paramref name="result"/> the indices (ascending, so in paint order) of the nodes whose boxes meet the rectangle.
    /// </summary>
    public void Query(double x0, double y0, double x1, double y1, List<int> result)
    {
        result.Clear();
        (int c0, int c1) = Range(x0, x1, _cellWidth, _columns);
        (int r0, int r1) = Range(y0, y1, _cellHeight, _rows);
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++)
            {
                result.AddRange(_cells[(r * _columns) + c]);
            }
        }

        result.AddRange(_large);
        result.Sort();
        int w = 0;
        int last = -1;
        for (int i = 0; i < result.Count; i++)
        {
            int idx = result[i];
            if (idx == last)
            {
                continue;
            }

            last = idx;
            RNode n = _nodes[idx];
            if (n.X1 >= x0 && n.X0 <= x1 && n.Y1 >= y0 && n.Y0 <= y1)
            {
                result[w++] = idx;
            }
        }

        result.RemoveRange(w, result.Count - w);
    }
}
