using System.Numerics;

namespace OneImage.Drawing;

/// <summary>One straight edge in device pixels, already clipped to the image: y within [0, height], x within [0, width].</summary>
internal struct Edge
{
    public float X0;
    public float Y0;
    public float X1;
    public float Y1;
}

/// <summary>
/// A path prepared for rasterizing into an image of a fixed size: flattened, clipped to the image and sorted into buckets by
/// tile row. Immutable once built, so every tile can read it from any thread.
/// <para>
/// Clipping rules (they keep winding exact for every pixel inside the image): the parts of an edge above or below the image are
/// cut off; the part left of x = 0 becomes a vertical edge at x = 0 with the same vertical extent (it still sets the winding
/// for everything to its right); the part right of x = width is dropped (it can only affect pixels further right).
/// </para>
/// </summary>
internal sealed class Raster
{
    private const int MaxEdges = 1 << 25;
    private const long MaxBucketEntries = 1L << 25;

    // A few rasters per thread are kept for the next scoped fill (see BuildScoped), with the buffers they grew.
    private const int MaxKeptEdges = 1 << 14;
    private const int MaxKeptItems = 1 << 15;
    private const int MaxKeptPoints = 1 << 16;

    [ThreadStatic]
    private static Raster? s_spare0;
    [ThreadStatic]
    private static Raster? s_spare1;
    [ThreadStatic]
    private static FlatPath? s_flat;

    private Edge[] _edges = new Edge[16];
    private int _count;
    private int[] _rowStart = [];
    private int[] _rowItems = [];
    private int[] _fill = [];
    private float _minX = float.MaxValue;
    private float _maxX = float.MinValue;
    private float _minY = float.MaxValue;
    private float _maxY = float.MinValue;
    private bool _rightClipped;

    private Raster(int width, int height)
    {
        Width = width;
        Height = height;
        TilesX = (width + Tile.Size - 1) >> Tile.Shift;
        TilesY = (height + Tile.Size - 1) >> Tile.Shift;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int TilesX { get; private set; }

    public int TilesY { get; private set; }

    public int MinTileX { get; private set; }

    public int MaxTileX { get; private set; } = -1;

    public int MinTileY { get; private set; }

    public int MaxTileY { get; private set; } = -1;

    public int EdgeCount => _count;

    public ReadOnlySpan<Edge> Edges => _edges.AsSpan(0, _count);

    /// <summary>The indices (into <see cref="Edges"/>) of the edges that touch tile row <paramref name="ty"/>.</summary>
    public ReadOnlySpan<int> RowEdges(int ty) => _rowItems.AsSpan(_rowStart[ty], _rowStart[ty + 1] - _rowStart[ty]);

    /// <summary>Whether tile (tx, ty) can change: false means nothing in the path touches it or reaches over it.</summary>
    public bool MayTouch(int tx, int ty) =>
        tx >= MinTileX && tx <= MaxTileX && ty >= MinTileY && ty <= MaxTileY && _rowStart[ty + 1] > _rowStart[ty];

    public static Raster Build(Path path, Matrix3x2 matrix, int width, int height, double tolerance)
    {
        FlatPath flat = Flattener.Flatten(path, matrix, tolerance, (0, 0, width, height));
        return Fill(new Raster(width, height), flat);
    }

    /// <summary>
    /// Like <see cref="Build"/>, for a raster that is used only by the calling operation and given back with <see cref="Recycle"/>: the
    /// flattened points, the edges and the buckets reuse the buffers of an earlier scoped raster on this thread, so a fill of a small
    /// shape allocates nothing. The result is identical to <see cref="Build"/>.
    /// </summary>
    public static Raster BuildScoped(Path path, Matrix3x2 matrix, int width, int height, double tolerance)
    {
        FlatPath flat = s_flat ?? new FlatPath(Path.MaxFlattenedPoints);
        s_flat = null;
        flat.Reset();
        try
        {
            Flattener.Flatten(path, matrix, tolerance, (0, 0, width, height), flat);
            Raster r;
            if (s_spare0 is { } a)
            {
                r = a;
                s_spare0 = null;
            }
            else if (s_spare1 is { } b)
            {
                r = b;
                s_spare1 = null;
            }
            else
            {
                r = new Raster(0, 0);
            }

            r.Reset(width, height);
            return Fill(r, flat);
        }
        finally
        {
            if (flat.Xy.Length <= MaxKeptPoints * 2)
            {
                s_flat = flat;
            }
        }
    }

    /// <summary>Gives a raster made by <see cref="BuildScoped"/> back for reuse. The raster must not be used afterwards, by any thread.</summary>
    public void Recycle()
    {
        if (_edges.Length > MaxKeptEdges || _rowItems.Length > MaxKeptItems)
        {
            return;
        }

        if (s_spare0 is null)
        {
            s_spare0 = this;
        }
        else
        {
            s_spare1 ??= this;
        }
    }

    private void Reset(int width, int height)
    {
        Width = width;
        Height = height;
        TilesX = (width + Tile.Size - 1) >> Tile.Shift;
        TilesY = (height + Tile.Size - 1) >> Tile.Shift;
        _count = 0;
        _minX = float.MaxValue;
        _maxX = float.MinValue;
        _minY = float.MaxValue;
        _maxY = float.MinValue;
        _rightClipped = false;
        MinTileX = 0;
        MaxTileX = -1;
        MinTileY = 0;
        MaxTileY = -1;
    }

    private static Raster Fill(Raster r, FlatPath flat)
    {
        int begin = 0;
        for (int f = 0; f < flat.FigureCount; f++)
        {
            int end = flat.Ends[f];
            int n = end - begin;
            if (n >= 3)
            {
                // Every figure is filled as if closed.
                double px = flat.Xy[2 * (end - 1)], py = flat.Xy[(2 * (end - 1)) + 1];
                for (int i = begin; i < end; i++)
                {
                    double qx = flat.Xy[2 * i], qy = flat.Xy[(2 * i) + 1];
                    r.AddEdge(px, py, qx, qy);
                    px = qx;
                    py = qy;
                }
            }

            begin = end;
        }

        r.Bucket();
        return r;
    }

    private void AddEdge(double x0, double y0, double x1, double y1)
    {
        double w = Width, h = Height;
        if (y0 == y1 || (y0 <= 0 && y1 <= 0) || (y0 >= h && y1 >= h))
        {
            return;
        }

        double dxdy = (x1 - x0) / (y1 - y0);
        if (y0 < 0)
        {
            x0 -= y0 * dxdy;
            y0 = 0;
        }
        else if (y0 > h)
        {
            x0 += (h - y0) * dxdy;
            y0 = h;
        }

        if (y1 < 0)
        {
            x1 -= y1 * dxdy;
            y1 = 0;
        }
        else if (y1 > h)
        {
            x1 += (h - y1) * dxdy;
            y1 = h;
        }

        if (y0 == y1)
        {
            return;
        }

        if (x0 >= w && x1 >= w)
        {
            _rightClipped = true;
            return;
        }

        if (x0 <= 0 && x1 <= 0)
        {
            Emit(0, y0, 0, y1);
            return;
        }

        if (x0 >= 0 && x0 <= w && x1 >= 0 && x1 <= w)
        {
            Emit(x0, y0, x1, y1);
            return;
        }

        // The edge crosses x = 0 and/or x = width: cut it there and treat each piece by where it lies.
        double dx = x1 - x0;
        double ta = -x0 / dx, tb = (w - x0) / dx;
        if (ta > tb)
        {
            (ta, tb) = (tb, ta);
        }

        Span<double> ts = stackalloc double[4];
        int k = 0;
        ts[k++] = 0;
        if (ta > 0 && ta < 1)
        {
            ts[k++] = ta;
        }

        if (tb > 0 && tb < 1)
        {
            ts[k++] = tb;
        }

        ts[k++] = 1;
        for (int i = 0; i + 1 < k; i++)
        {
            double t0 = ts[i], t1 = ts[i + 1];
            double ax = x0 + (dx * t0), ay = i == 0 ? y0 : y0 + ((y1 - y0) * t0);
            double bx = x0 + (dx * t1), by = i + 2 == k ? y1 : y0 + ((y1 - y0) * t1);
            double mid = (ax + bx) / 2;
            if (mid <= 0)
            {
                Emit(0, ay, 0, by);
            }
            else if (mid >= w)
            {
                _rightClipped = true;
            }
            else
            {
                Emit(Math.Clamp(ax, 0, w), ay, Math.Clamp(bx, 0, w), by);
            }
        }
    }

    private void Emit(double x0, double y0, double x1, double y1)
    {
        float fy0 = (float)y0, fy1 = (float)y1;
        if (fy0 == fy1)
        {
            return;
        }

        if (_count == MaxEdges)
        {
            throw new ImageLimitException($"The path produces more than {MaxEdges} edges inside the image.");
        }

        if (_count == _edges.Length)
        {
            Array.Resize(ref _edges, _edges.Length * 2);
        }

        float fx0 = (float)x0, fx1 = (float)x1;
        _edges[_count++] = new Edge { X0 = fx0, Y0 = fy0, X1 = fx1, Y1 = fy1 };
        _minX = Math.Min(_minX, Math.Min(fx0, fx1));
        _maxX = Math.Max(_maxX, Math.Max(fx0, fx1));
        _minY = Math.Min(_minY, Math.Min(fy0, fy1));
        _maxY = Math.Max(_maxY, Math.Max(fy0, fy1));
    }

    private void Bucket()
    {
        if (_rowStart.Length < TilesY + 1)
        {
            _rowStart = new int[TilesY + 1];
        }
        else
        {
            Array.Clear(_rowStart, 0, TilesY + 1);
        }

        if (_count == 0)
        {
            return;
        }

        MinTileX = Math.Clamp((int)_minX >> Tile.Shift, 0, TilesX - 1);
        MaxTileX = _rightClipped ? TilesX - 1 : Math.Clamp(((int)Math.Ceiling(_maxX) - 1) >> Tile.Shift, MinTileX, TilesX - 1);
        MinTileY = Math.Clamp((int)_minY >> Tile.Shift, 0, TilesY - 1);
        MaxTileY = Math.Clamp(((int)Math.Ceiling(_maxY) - 1) >> Tile.Shift, MinTileY, TilesY - 1);

        long total = 0;
        for (int i = 0; i < _count; i++)
        {
            RowRange(in _edges[i], out int r0, out int r1);
            total += r1 - r0 + 1;
            for (int r = r0; r <= r1; r++)
            {
                _rowStart[r + 1]++;
            }
        }

        if (total > MaxBucketEntries)
        {
            throw new ImageLimitException("The path crosses too many tile rows (more than 2^25 edge-row pairs).");
        }

        for (int r = 0; r < TilesY; r++)
        {
            _rowStart[r + 1] += _rowStart[r];
        }

        if (_rowItems.Length < total)
        {
            _rowItems = new int[(int)total];
        }

        if (_fill.Length < TilesY)
        {
            _fill = new int[TilesY];
        }
        else
        {
            Array.Clear(_fill, 0, TilesY);
        }

        int[] fill = _fill;
        for (int i = 0; i < _count; i++)
        {
            RowRange(in _edges[i], out int r0, out int r1);
            for (int r = r0; r <= r1; r++)
            {
                _rowItems[_rowStart[r] + fill[r]++] = i;
            }
        }
    }

    private void RowRange(in Edge e, out int r0, out int r1)
    {
        float lo = Math.Min(e.Y0, e.Y1), hi = Math.Max(e.Y0, e.Y1);
        r0 = Math.Min((int)lo >> Tile.Shift, TilesY - 1);
        r1 = Math.Clamp(((int)Math.Ceiling(hi) - 1) >> Tile.Shift, r0, TilesY - 1);
    }
}
