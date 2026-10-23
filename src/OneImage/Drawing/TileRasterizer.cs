using System.Collections.Concurrent;

namespace OneImage.Drawing;

/// <summary>Per-call working memory for rasterizing and painting one tile; pooled so a render allocates nothing per tile.</summary>
internal sealed class RasterScratch
{
    private static readonly ConcurrentBag<RasterScratch> Pool = [];

    /// <summary>Coverage accumulation: one row of Tile.Size + 2 cells per tile row (see <see cref="TileRasterizer"/>).</summary>
    public readonly float[] Accum = new float[(Tile.Size + 2) * Tile.Size];

    /// <summary>Final coverage 0..1, Tile.Size cells per row. Only the span of each row listed in <see cref="SpanStart"/>/<see cref="SpanEnd"/> is meaningful.</summary>
    public readonly float[] Coverage = new float[Tile.Size * Tile.Size];

    public readonly int[] SpanStart = new int[Tile.Size];
    public readonly int[] SpanEnd = new int[Tile.Size];
    public readonly bool[] Dirty = new bool[Tile.Size];

    // The first and last accumulator cell an edge touched in each dirty row, so only that part is read and cleared again.
    public readonly int[] RowMin = new int[Tile.Size];
    public readonly int[] RowMax = new int[Tile.Size];

    // Accum is all zero between uses; this is set while it may not be (a use that was cut short), and the next use clears it whole.
    public bool AccumUnclean;

    /// <summary>The run of fills being computed together (see <c>FillNode</c>).</summary>
    public readonly List<FillNode> Layers = new(8);

    public Crossing[] Crossings = new Crossing[256];
    public int CrossingCount;

    // Row buffers for the general blend path: backdrop, overlay and result in working format (up to 4 floats per pixel).
    public readonly float[] Back = new float[Tile.Size * 4];
    public readonly float[] Over = new float[Tile.Size * 4];
    public readonly float[] Result = new float[Tile.Size * 4];
    public readonly float[] BrushRow = new float[Tile.Size * 4];
    public readonly float[] Straight = new float[Tile.Size * 4];

    // One decoded run of 8-bit pixels as straight floats (linear-light fills).
    public readonly byte[] LinearBytes = new byte[Tile.Size * 16];

    // The scratch a thread used last is kept for that thread: tile workers rent and return in a tight loop, and a shared bag
    // would make them contend on its lock for every tile. Scratch returned while the slot is taken goes to the shared bag.
    [ThreadStatic]
    private static RasterScratch? t_own;

    public static RasterScratch Rent()
    {
        RasterScratch? s = t_own;
        if (s is not null)
        {
            t_own = null;
            return s;
        }

        return Pool.TryTake(out s) ? s : new RasterScratch();
    }

    public static void Return(RasterScratch s)
    {
        if (t_own is null)
        {
            t_own = s;
        }
        else
        {
            Pool.Add(s);
        }
    }

    public readonly record struct Crossing(int Row, float X, int Dir);
}

/// <summary>
/// Turns the edges of one tile row into per-pixel coverage for one 128×128 tile.
/// <para>
/// <b>Anti-aliased coverage</b> is exact area: each edge adds, to the cells it passes through, the signed area to the right of
/// it inside that pixel (the signed-area accumulation of font-rs and stb_truetype). A running sum along the row then gives the
/// signed winding-weighted area covered. Non-zero takes <c>min(|sum|, 1)</c>; even-odd folds the sum into a triangle wave of
/// period 2. This is exact for shapes whose edges do not overlap inside one pixel; where edges of opposite winding cross in a
/// single pixel the signed areas cancel, which slightly under-covers that pixel. Sums closer than 1/1024 to 0 or 1 snap to
/// them so float rounding never leaves faint speckles.
/// </para>
/// <para>
/// <b>Aliased coverage</b> samples the shape at pixel centers: a pixel is inside when the point (x + 0.5, y + 0.5) is, with the
/// scanline's edge crossings counted by the chosen rule; the result is 0 or 1.
/// </para>
/// </summary>
internal static class TileRasterizer
{
    private const float Snap = 1f / 1024f;
    private static readonly CrossingComparer Comparer = new();

    /// <summary>Computes coverage for tile (tx, ty), whose valid size is <paramref name="tw"/>×<paramref name="th"/>. Returns false when no pixel has coverage.</summary>
    public static bool Rasterize(Raster raster, int tx, int ty, int tw, int th, RasterScratch s, bool antialias, IntersectionRule rule)
    {
        ReadOnlySpan<int> items = raster.RowEdges(ty);
        if (items.Length == 0)
        {
            return false;
        }

        return antialias ? Antialiased(raster.Edges, items, tx << Tile.Shift, ty << Tile.Shift, tw, th, s, rule) : Aliased(raster.Edges, items, tx << Tile.Shift, ty << Tile.Shift, tw, th, s, rule);
    }

    /// <summary>
    /// Multiplies the coverage already in <paramref name="s"/> by the coverage of a clip raster for the same tile and narrows each
    /// row's span to where both are non-zero. Returns false when no pixel keeps any coverage.
    /// </summary>
    public static bool ApplyClip(Raster clip, int tx, int ty, int tw, int th, RasterScratch s, bool antialias, IntersectionRule rule)
    {
        RasterScratch c = RasterScratch.Rent();
        try
        {
            if (!Rasterize(clip, tx, ty, tw, th, c, antialias, rule))
            {
                return false;
            }

            bool any = false;
            for (int r = 0; r < th; r++)
            {
                int a = Math.Max(s.SpanStart[r], c.SpanStart[r]);
                int b = Math.Min(s.SpanEnd[r], c.SpanEnd[r]);
                if (a >= b || s.SpanStart[r] >= s.SpanEnd[r] || c.SpanStart[r] >= c.SpanEnd[r])
                {
                    s.SpanStart[r] = 0;
                    s.SpanEnd[r] = 0;
                    continue;
                }

                Span<float> cov = s.Coverage.AsSpan((r * Tile.Size) + a, b - a);
                ReadOnlySpan<float> cc = c.Coverage.AsSpan((r * Tile.Size) + a, b - a);
                for (int x = 0; x < cov.Length; x++)
                {
                    cov[x] *= cc[x];
                }

                s.SpanStart[r] = a;
                s.SpanEnd[r] = b;
                any = true;
            }

            return any;
        }
        finally
        {
            RasterScratch.Return(c);
        }
    }

    private static bool Antialiased(ReadOnlySpan<Edge> edges, ReadOnlySpan<int> items, int ox, int oy, int tw, int th, RasterScratch s, IntersectionRule rule)
    {
        const int stride = Tile.Size + 2;
        if (s.AccumUnclean)
        {
            Array.Clear(s.Accum);
        }

        s.AccumUnclean = true;
        Array.Clear(s.Dirty, 0, th);
        foreach (int index in items)
        {
            ref readonly Edge e = ref edges[index];
            if (Math.Min(e.X0, e.X1) >= ox + tw)
            {
                continue; // entirely right of the tile: no effect on it
            }

            Accumulate(s, e.X0 - ox, e.Y0 - oy, e.X1 - ox, e.Y1 - oy, tw, th);
        }

        bool any = false;
        for (int r = 0; r < th; r++)
        {
            s.SpanStart[r] = 0;
            s.SpanEnd[r] = 0;
            if (!s.Dirty[r])
            {
                continue;
            }

            // Cells left of the first touched one are zero, so the running sum is 0 and the coverage 0 there; right of the last
            // touched one the sum no longer changes, so the coverage is constant. Only the touched cells are summed.
            int from = s.RowMin[r];
            int to = Math.Min(s.RowMax[r] + 1, tw);
            Span<float> accRow = s.Accum.AsSpan(r * stride, stride);
            Span<float> cov = s.Coverage.AsSpan(r * Tile.Size, tw);
            float sum = 0;
            int first = -1, last = -1;
            for (int x = from; x < to; x++)
            {
                sum += accRow[x];
                float c = CoverageOf(sum, rule);
                cov[x] = c;
                if (c > 0f)
                {
                    if (first < 0)
                    {
                        first = x;
                    }

                    last = x;
                }
            }

            if (to < tw)
            {
                float c = CoverageOf(sum, rule);
                if (c > 0f)
                {
                    cov[to..].Fill(c);
                    if (first < 0)
                    {
                        first = to;
                    }

                    last = tw - 1;
                }
            }

            // Leave the accumulator zero for the next edge set (cells up to RowMax can hold values, including the one past the tile).
            accRow[from..Math.Min(s.RowMax[r] + 1, stride - 1)].Clear();
            accRow[stride - 1] = 0f;
            if (first >= 0)
            {
                s.SpanStart[r] = first;
                s.SpanEnd[r] = last + 1;
                any = true;
            }
        }

        s.AccumUnclean = false;
        return any;
    }

    private static float CoverageOf(float sum, IntersectionRule rule)
    {
        float a = MathF.Abs(sum);
        float c;
        if (rule == IntersectionRule.NonZero)
        {
            c = a > 1f ? 1f : a;
        }
        else
        {
            a -= 2f * MathF.Floor(a * 0.5f);
            c = a > 1f ? 2f - a : a;
        }

        if (c < Snap)
        {
            return 0f;
        }

        return c > 1f - Snap ? 1f : c;
    }

    // Adds one edge (tile-local coordinates) to the accumulation buffer, cutting it to the tile's columns first.
    private static void Accumulate(RasterScratch s, float x0, float y0, float x1, float y1, int tw, int th)
    {
        if (y0 == y1)
        {
            return;
        }

        float dir = 1f;
        if (y0 > y1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            dir = -1f;
        }

        if (y1 <= 0 || y0 >= th)
        {
            return;
        }

        float dxdy = (x1 - x0) / (y1 - y0);
        if (y0 < 0)
        {
            x0 -= y0 * dxdy;
            y0 = 0;
        }

        if (y1 > th)
        {
            x1 -= (y1 - th) * dxdy;
            y1 = th;
        }

        float w = tw;
        if (x0 >= w && x1 >= w)
        {
            return;
        }

        if (x0 <= 0 && x1 <= 0)
        {
            Line(s, 0, y0, 0, y1, dir, tw, th);
            return;
        }

        if (x0 >= 0 && x0 <= w && x1 >= 0 && x1 <= w)
        {
            Line(s, x0, y0, x1, y1, dir, tw, th);
            return;
        }

        // Crosses the left and/or right column boundary: left of it the edge acts as a vertical at x = 0, right of it it is dropped.
        float dx = x1 - x0;
        float ta = -x0 / dx, tb = (w - x0) / dx;
        if (ta > tb)
        {
            (ta, tb) = (tb, ta);
        }

        Span<float> ts = [0f, 0f, 0f, 0f];
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
        float dy = y1 - y0;
        for (int i = 0; i + 1 < k; i++)
        {
            float ax = x0 + (dx * ts[i]), ay = i == 0 ? y0 : y0 + (dy * ts[i]);
            float bx = x0 + (dx * ts[i + 1]), by = i + 2 == k ? y1 : y0 + (dy * ts[i + 1]);
            float mid = (ax + bx) * 0.5f;
            if (mid <= 0)
            {
                Line(s, 0, ay, 0, by, dir, tw, th);
            }
            else if (mid < w)
            {
                Line(s, Math.Clamp(ax, 0f, w), ay, Math.Clamp(bx, 0f, w), by, dir, tw, th);
            }
        }
    }

    // y0 < y1, x within [0, tw]. Splits the edge by pixel row, and each row by pixel column, adding signed trapezoid areas.
    private static void Line(RasterScratch s, float x0, float y0, float x1, float y1, float dir, int tw, int th)
    {
        const int stride = Tile.Size + 2;
        if (y1 <= y0)
        {
            return;
        }

        float dxdy = (x1 - x0) / (y1 - y0);
        float x = x0;
        int rowStart = (int)y0;
        int rowEnd = Math.Min((int)MathF.Ceiling(y1), th);
        float fw = tw;
        Span<float> acc = s.Accum;
        for (int r = rowStart; r < rowEnd; r++)
        {
            float top = MathF.Max(r, y0), bottom = MathF.Min(r + 1, y1);
            float dy = bottom - top;
            float xnext = x + (dxdy * dy);
            float d = dy * dir;
            float xa = x < xnext ? x : xnext, xb = x < xnext ? xnext : x;
            xa = MathF.Max(xa, 0f);
            xb = MathF.Min(xb, fw);
            int o = r * stride;
            float xaFloor = MathF.Floor(xa);
            int xi0 = (int)xaFloor;
            int xi1 = (int)MathF.Ceiling(xb);
            int xEnd = xi1 <= xi0 + 1 ? xi0 + 1 : xi1;
            if (s.Dirty[r])
            {
                if (xi0 < s.RowMin[r])
                {
                    s.RowMin[r] = xi0;
                }

                if (xEnd > s.RowMax[r])
                {
                    s.RowMax[r] = xEnd;
                }
            }
            else
            {
                s.Dirty[r] = true;
                s.RowMin[r] = xi0;
                s.RowMax[r] = xEnd;
            }

            if (xi1 <= xi0 + 1)
            {
                float xmf = (0.5f * (xa + xb)) - xaFloor;
                acc[o + xi0] += d - (d * xmf);
                acc[o + xi0 + 1] += d * xmf;
            }
            else
            {
                float inv = 1f / (xb - xa);
                float xaf = xa - xaFloor;
                float a0 = 0.5f * inv * (1f - xaf) * (1f - xaf);
                float xbf = xb - xi1 + 1f;
                float am = 0.5f * inv * xbf * xbf;
                acc[o + xi0] += d * a0;
                if (xi1 == xi0 + 2)
                {
                    acc[o + xi0 + 1] += d * (1f - a0 - am);
                }
                else
                {
                    float a1 = inv * (1.5f - xaf);
                    acc[o + xi0 + 1] += d * (a1 - a0);
                    for (int xi = xi0 + 2; xi < xi1 - 1; xi++)
                    {
                        acc[o + xi] += d * inv;
                    }

                    float a2 = a1 + ((xi1 - xi0 - 3) * inv);
                    acc[o + xi1 - 1] += d * (1f - a2 - am);
                }

                acc[o + xi1] += d * am;
            }

            x = xnext;
        }
    }

    private static bool Aliased(ReadOnlySpan<Edge> edges, ReadOnlySpan<int> items, int ox, int oy, int tw, int th, RasterScratch s, IntersectionRule rule)
    {
        s.CrossingCount = 0;
        foreach (int index in items)
        {
            ref readonly Edge e = ref edges[index];
            float ylo = Math.Min(e.Y0, e.Y1) - oy, yhi = Math.Max(e.Y0, e.Y1) - oy;
            // Rows whose center y + 0.5 lies in [ylo, yhi).
            int r0 = Math.Max(0, (int)MathF.Ceiling(ylo - 0.5f));
            int r1 = Math.Min(th, (int)MathF.Ceiling(yhi - 0.5f));
            if (r0 >= r1)
            {
                continue;
            }

            float dxdy = (e.X1 - e.X0) / (e.Y1 - e.Y0);
            int dir = e.Y1 > e.Y0 ? 1 : -1;
            for (int r = r0; r < r1; r++)
            {
                float yc = oy + r + 0.5f;
                float x = e.X0 + ((yc - e.Y0) * dxdy) - ox;
                if (s.CrossingCount == s.Crossings.Length)
                {
                    Array.Resize(ref s.Crossings, s.Crossings.Length * 2);
                }

                s.Crossings[s.CrossingCount++] = new RasterScratch.Crossing(r, x, dir);
            }
        }

        Array.Clear(s.SpanStart, 0, th);
        Array.Clear(s.SpanEnd, 0, th);
        if (s.CrossingCount == 0)
        {
            return false;
        }

        Array.Sort(s.Crossings, 0, s.CrossingCount, Comparer);
        bool any = false;
        int i = 0;
        while (i < s.CrossingCount)
        {
            int row = s.Crossings[i].Row;
            int j = i;
            while (j < s.CrossingCount && s.Crossings[j].Row == row)
            {
                j++;
            }

            Span<float> cov = s.Coverage.AsSpan(row * Tile.Size, tw);
            cov.Clear();
            int first = int.MaxValue, last = -1;
            int winding = 0;
            for (int k = i; k < j; k++)
            {
                winding += s.Crossings[k].Dir;
                bool inside = rule == IntersectionRule.NonZero ? winding != 0 : (winding & 1) != 0;
                if (!inside)
                {
                    continue;
                }

                // Pixels whose center x + 0.5 lies in [xa, xb).
                int a = Math.Max(0, (int)MathF.Ceiling(s.Crossings[k].X - 0.5f));
                // Edges right of the image were dropped, so after the last crossing an inside run goes on to the tile end.
                int b = k + 1 < j ? Math.Min(tw, (int)MathF.Ceiling(s.Crossings[k + 1].X - 0.5f)) : tw;
                if (a < b)
                {
                    cov[a..b].Fill(1f);
                    first = Math.Min(first, a);
                    last = Math.Max(last, b);
                }
            }

            if (last > 0)
            {
                s.SpanStart[row] = first;
                s.SpanEnd[row] = last;
                any = true;
            }

            i = j;
        }

        return any;
    }

    private sealed class CrossingComparer : IComparer<RasterScratch.Crossing>
    {
        public int Compare(RasterScratch.Crossing a, RasterScratch.Crossing b) =>
            a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.X.CompareTo(b.X);
    }
}
