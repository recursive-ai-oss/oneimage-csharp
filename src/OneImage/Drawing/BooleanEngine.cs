using System.Buffers;
using System.Numerics;

namespace OneImage.Drawing;

/// <summary>
/// Polygon boolean operations on a snapped integer grid.
/// <para>
/// Pipeline: flatten both operands; snap every vertex to a grid of 1/256 pixel; split segments wherever they cross, touch or
/// overlap until no two segments meet anywhere except at shared end points (a crossing is rounded to the grid and both segments
/// are routed through the rounded point, which can create a new crossing nearby, so this repeats for the segments that moved);
/// merge identical segments by adding their winding contributions; sweep over the horizontal lines through all vertices, where
/// the segments inside one strip never cross and so have a fixed left-to-right order, and use the running winding numbers of
/// both operands to decide which segments bound the result; finally chain the bounding segments into closed contours.
/// All geometric decisions use exact integer arithmetic, so the result never depends on rounding luck or on thread count.
/// </para>
/// <para>
/// Because winding contributions are added per segment, coincident and opposite edges, spikes and self-overlaps need no special
/// cases: they cancel or accumulate like any other edges. The sweep treats a horizontal segment by the windings above it and
/// the segment's own contribution.
/// </para>
/// </summary>
internal static class BooleanEngine
{
    public const int Scale = 256;

    private const double MaxCoordinate = 1 << 25;
    private const int MaxInputSegments = 1 << 22;
    private const int MaxSegments = 1 << 23;
    private const int MaxRounds = 64;
    private const long MaxPairTests = 1L << 31;
    private const int MinLevel = 3;

    internal struct Seg
    {
        public long X0;
        public long Y0;
        public long X1;
        public long Y1;
        public int Ds; // winding added for the subject when crossing this segment left to right (top-to-bottom segments: +1)
        public int Dc;
        public bool Dirty; // geometry changed in the last round, so it must be compared with everything again
    }

    private struct Split
    {
        public int Seg;
        public long X;
        public long Y;
        public bool Proper;
    }

    private struct BEdge
    {
        public long Fx;
        public long Fy;
        public long Tx;
        public long Ty;
    }

    [ThreadStatic]
    private static Scratch? t_scratch;

    /// <summary>Working arrays kept per thread between calls, so a repeated operation allocates little.</summary>
    private sealed class Scratch
    {
        public byte[] Level = [];
        public byte[] Elv = [];
        public int[] Ecx = [];
        public int[] Ecy = [];
        public int[] Eid = [];
        public int[] Head = [];
        public int[] Next = [];
        public int[] Seen = [];
        public int[] ByStart = [];
        public int[] ByEnd = [];
        public int[] ByHor = [];
        public int[] Mark = [];
        public int[] Active = [];
        public int[] Order = [];
        public int[] Link = [];
        public long[] StartKey = [];
        public long[] EndKey = [];
        public long[] HorKey = [];
        public double[] Slope = [];
        public BEdge[] Edges = [];
        public bool[] SeenEdge = [];

        public bool IsLarge => Ecx.Length > (1 << 19) || StartKey.Length > (1 << 19) || Edges.Length > (1 << 19);

        public static void Need<T>(ref T[] array, int n)
        {
            if (array.Length < n)
            {
                array = new T[Math.Max(n, Math.Max(16, array.Length * 2))];
            }
        }

        public static void Grow<T>(ref T[] array, int used)
        {
            var bigger = new T[Math.Max(16, array.Length * 2)];
            Array.Copy(array, bigger, used);
            array = bigger;
        }
    }

    private sealed class SplitList
    {
        public Split[] Items = new Split[64];
        public int Count;
        public bool Collinear; // some pair lay on one line, so identical pieces can appear

        public void Add(int seg, long x, long y, bool proper)
        {
            if (Count == Items.Length)
            {
                Array.Resize(ref Items, Count * 2);
            }

            Items[Count++] = new Split { Seg = seg, X = x, Y = y, Proper = proper };
        }
    }

    /// <summary>Combines the regions of two sets of paths. Segments of the subject count toward its winding, those of the clip toward theirs.</summary>
    public static Path Run(ReadOnlySpan<Path> subject, ReadOnlySpan<Path> clip, BooleanOperation op, IntersectionRule subjectRule, IntersectionRule clipRule, float tolerance)
    {
        Seg[] segs = Pools<Seg>.Pool.Rent(256);
        Scratch sc = t_scratch ?? new Scratch();
        t_scratch = null;
        try
        {
            int n = 0;
            foreach (Path p in subject)
            {
                AddPath(ref segs, ref n, p, 0, tolerance);
            }

            foreach (Path p in clip)
            {
                AddPath(ref segs, ref n, p, 1, tolerance);
            }

            if (n == 0)
            {
                return Path.Empty;
            }

            int count = Resolve(ref segs, ref n, op, subjectRule, clipRule, sc);
            return Assemble(sc, count);
        }
        finally
        {
            Pools<Seg>.Pool.Return(segs);
            if (!sc.IsLarge)
            {
                t_scratch = sc; // reused by the next call on this thread unless it grew big
            }
        }
    }

    // Pooled arrays that must keep their contents when they grow.
    private static void Grow<T>(ref T[] array, int used)
    {
        T[] bigger = Pools<T>.Pool.Rent(Math.Max(16, array.Length * 2));
        Array.Copy(array, bigger, used);
        Pools<T>.Pool.Return(array);
        array = bigger;
    }

    /// <summary>
    /// Splits segments until they meet only at end points, then sweeps. Rounding a crossing to the grid moves the pieces a little,
    /// which can create a new crossing nearby; the sweep checks every pair of segments that become neighbors and, when it finds
    /// one, reports it so the pieces that moved are compared with everything once more.
    /// </summary>
    private static int Resolve(ref Seg[] segs, ref int n, BooleanOperation op, IntersectionRule sr, IntersectionRule cr, Scratch sc)
    {
        bool moved = false, collinear = false, merged = false;
        for (int round = 0; ; round++)
        {
            if (round > MaxRounds)
            {
                throw new ImageOperationException("The boolean operation could not settle the geometry; the input is too degenerate.");
            }

            var splits = new SplitList();
            FindSplits(segs, n, splits, sc);
            bool proper = false;
            for (int i = 0; i < splits.Count; i++)
            {
                proper |= splits.Items[i].Proper;
            }

            if (splits.Count > 0)
            {
                Rebuild(ref segs, ref n, splits);
            }
            else if (round > 0)
            {
                throw new ImageOperationException("The boolean operation found an inconsistent arrangement.");
            }

            moved |= proper;
            collinear |= splits.Collinear;
            while (true)
            {
                if (Sweep(segs, n, op, sr, cr, moved || collinear, sc, out int count, out bool duplicate))
                {
                    return count;
                }

                if (!duplicate)
                {
                    break; // a crossing: compare the pieces that moved with everything once more
                }

                if (merged)
                {
                    throw new ImageOperationException("The boolean operation found an inconsistent arrangement.");
                }

                MergeDuplicates(segs, ref n);
                merged = true;
            }

            if (!moved)
            {
                throw new ImageOperationException("The boolean operation found an inconsistent arrangement.");
            }

            merged = false;
        }
    }

    // ---- Input ----

    private static long Snap(float v)
    {
        double d = v;
        if (!(Math.Abs(d) <= MaxCoordinate))
        {
            throw new ImageLimitException("Boolean operations accept coordinates within ±33554432 pixels.");
        }

        return (long)Math.Round(d * Scale);
    }

    private static void AddPath(ref Seg[] segs, ref int n, Path path, int operand, float tolerance)
    {
        FlatPath flat = Flattener.Flatten(path, System.Numerics.Matrix3x2.Identity, tolerance, null);
        int begin = 0;
        for (int f = 0; f < flat.FigureCount; f++)
        {
            int end = flat.Ends[f];
            if (end - begin >= 3)
            {
                long px = Snap(flat.Xy[2 * (end - 1)]), py = Snap(flat.Xy[(2 * (end - 1)) + 1]);
                for (int i = begin; i < end; i++)
                {
                    long qx = Snap(flat.Xy[2 * i]), qy = Snap(flat.Xy[(2 * i) + 1]);
                    AddEdge(ref segs, ref n, px, py, qx, qy, operand);
                    px = qx;
                    py = qy;
                }
            }

            begin = end;
        }
    }

    private static void AddEdge(ref Seg[] segs, ref int n, long x0, long y0, long x1, long y1, int operand)
    {
        if (x0 == x1 && y0 == y1)
        {
            return;
        }

        int sign = 1;
        if (y1 < y0 || (y1 == y0 && x1 < x0))
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            sign = -1;
        }

        if (n == MaxInputSegments)
        {
            throw new ImageLimitException($"A boolean operation accepts at most {MaxInputSegments} segments.");
        }

        if (n == segs.Length)
        {
            Grow(ref segs, n);
        }

        segs[n++] = new Seg { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Ds = operand == 0 ? sign : 0, Dc = operand == 1 ? sign : 0, Dirty = true };
    }

    // ---- Exact predicates ----

    /// <summary>The sign of ux·vy − uy·vx.</summary>
    private static int Cross(long ux, long uy, long vx, long vy)
    {
        long m = Math.Max(Math.Max(Math.Abs(ux), Math.Abs(uy)), Math.Max(Math.Abs(vx), Math.Abs(vy)));
        if (m < (1L << 30))
        {
            return Math.Sign((ux * vy) - (uy * vx));
        }

        return Int128.Sign(((Int128)ux * vy) - ((Int128)uy * vx));
    }

    private static Int128 CrossValue(long ux, long uy, long vx, long vy) => ((Int128)ux * vy) - ((Int128)uy * vx);

    private static int Dot(long ux, long uy, long vx, long vy) => Int128.Sign(((Int128)ux * vx) + ((Int128)uy * vy));

    // ---- Arrangement: split until segments meet only at end points ----

    private static void MergeDuplicates(Seg[] segs, ref int n)
    {
        segs.AsSpan(0, n).Sort(static (a, b) =>
        {
            int c = a.Y0.CompareTo(b.Y0);
            if (c != 0) { return c; }
            c = a.X0.CompareTo(b.X0);
            if (c != 0) { return c; }
            c = a.Y1.CompareTo(b.Y1);
            return c != 0 ? c : a.X1.CompareTo(b.X1);
        });
        int w = 0;
        for (int i = 0; i < n; i++)
        {
            if (w > 0 && segs[w - 1].X0 == segs[i].X0 && segs[w - 1].Y0 == segs[i].Y0 && segs[w - 1].X1 == segs[i].X1 && segs[w - 1].Y1 == segs[i].Y1)
            {
                segs[w - 1].Ds += segs[i].Ds;
                segs[w - 1].Dc += segs[i].Dc;
                segs[w - 1].Dirty |= segs[i].Dirty;
            }
            else
            {
                segs[w++] = segs[i];
            }
        }

        // A segment that adds nothing to either winding separates equal regions on its two sides.
        int k = 0;
        for (int i = 0; i < w; i++)
        {
            if (segs[i].Ds != 0 || segs[i].Dc != 0)
            {
                segs[k++] = segs[i];
            }
        }

        n = k;
    }

    private static void Rebuild(ref Seg[] segs, ref int n, SplitList splits)
    {
        Span<Split> all = splits.Items.AsSpan(0, splits.Count);
        all.Sort(static (a, b) => a.Seg.CompareTo(b.Seg));
        Seg[] next = Pools<Seg>.Pool.Rent(Math.Max(16, n + splits.Count + 16));
        int m = 0;
        int s = 0;
        Int128[] keys = new Int128[16];
        Split[] items = new Split[16];
        for (int i = 0; i < n; i++)
        {
            if (s >= all.Length || all[s].Seg != i)
            {
                Push(ref next, ref m, segs[i], false);
                continue;
            }

            int e = s;
            bool proper = false;
            while (e < all.Length && all[e].Seg == i)
            {
                proper |= all[e].Proper;
                e++;
            }

            Seg sg = segs[i];
            int cnt = e - s;
            if (keys.Length < cnt)
            {
                keys = new Int128[cnt * 2];
                items = new Split[cnt * 2];
            }

            long dx = sg.X1 - sg.X0, dy = sg.Y1 - sg.Y0;
            for (int k = 0; k < cnt; k++)
            {
                items[k] = all[s + k];
                keys[k] = ((Int128)(all[s + k].X - sg.X0) * dx) + ((Int128)(all[s + k].Y - sg.Y0) * dy);
            }

            Array.Sort(keys, items, 0, cnt);
            long cx = sg.X0, cy = sg.Y0;
            for (int k = 0; k <= cnt; k++)
            {
                long nx, ny;
                if (k < cnt)
                {
                    nx = items[k].X;
                    ny = items[k].Y;
                }
                else
                {
                    nx = sg.X1;
                    ny = sg.Y1;
                }

                if (nx == cx && ny == cy)
                {
                    continue;
                }

                // Pieces run along the original direction; a piece that points up (rounding) is stored flipped with its winding negated.
                int ds = sg.Ds, dc = sg.Dc;
                long ax = cx, ay = cy, bx = nx, by = ny;
                if (by < ay || (by == ay && bx < ax))
                {
                    (ax, bx) = (bx, ax);
                    (ay, by) = (by, ay);
                    ds = -ds;
                    dc = -dc;
                }

                Push(ref next, ref m, new Seg { X0 = ax, Y0 = ay, X1 = bx, Y1 = by, Ds = ds, Dc = dc }, proper);
                cx = nx;
                cy = ny;
            }

            s = e;
        }

        Pools<Seg>.Pool.Return(segs);
        segs = next;
        n = m;
    }

    private static void Push(ref Seg[] arr, ref int m, Seg s, bool isDirty)
    {
        if (m == MaxSegments)
        {
            throw new ImageLimitException($"A boolean operation produced more than {MaxSegments} segments.");
        }

        if (m == arr.Length)
        {
            Grow(ref arr, m);
        }

        s.Dirty = isDirty;
        arr[m++] = s;
    }

    // ---- Candidate pairs: a hash grid per power-of-two cell size ----
    //
    // A segment lives at the level whose cell is at least as long as the segment, so it touches few cells; segments much longer
    // than the typical one are stored at a finer level, in the cells along their line only, so a long diagonal edge meets just
    // the small segments near it. A segment looks for partners in its own level and every coarser one; a pair is examined at
    // most once per looking segment, and when both look at each other (equal levels) only the lower index does.

    private const int MaxRows = 80;
    private const int MaxGridEntries = 1 << 26;

    private static int LevelOf(long size) => size <= (1L << MinLevel) ? MinLevel : 64 - BitOperations.LeadingZeroCount((ulong)(size - 1));

    private static uint CellHash(long cx, long cy, int l)
    {
        ulong h = ((ulong)cx * 0x9E3779B97F4A7C15UL) ^ ((ulong)cy * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)l * 0x165667B19E3779F9UL);
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        return (uint)(h >> 32);
    }

    // The cells a segment passes through at level l, as per-row column ranges (conservative by one grid unit).
    private static int Rows(in Seg s, int l, Span<long> rowY, Span<long> rowC0, Span<long> rowC1)
    {
        long r0 = s.Y0 >> l, r1 = s.Y1 >> l;
        long dx = s.X1 - s.X0, dy = s.Y1 - s.Y0;
        int count = 0;
        long bx0 = Math.Min(s.X0, s.X1) >> l, bx1 = Math.Max(s.X0, s.X1) >> l;
        if (r1 - r0 <= 1 && bx1 - bx0 <= 1)
        {
            // Small against the cell: the cells of its box are few, no need to follow the line.
            for (long r = r0; r <= r1; r++)
            {
                rowY[count] = r;
                rowC0[count] = bx0;
                rowC1[count] = bx1;
                count++;
            }

            return count;
        }

        for (long r = r0; r <= r1; r++)
        {
            double xa, xb;
            if (dy == 0)
            {
                xa = Math.Min(s.X0, s.X1);
                xb = Math.Max(s.X0, s.X1);
            }
            else
            {
                long top = Math.Max(s.Y0, r << l), bottom = Math.Min(s.Y1, (r + 1) << l);
                double ta = s.X0 + ((double)(top - s.Y0) * dx / dy), tb = s.X0 + ((double)(bottom - s.Y0) * dx / dy);
                xa = Math.Min(ta, tb);
                xb = Math.Max(ta, tb);
            }

            rowY[count] = r;
            rowC0[count] = ((long)Math.Floor(xa) - 1) >> l;
            rowC1[count] = ((long)Math.Ceiling(xb) + 1) >> l;
            count++;
        }

        return count;
    }

    private static void FindSplits(Seg[] segs, int n, SplitList splits, Scratch sc)
    {
        const int Levels = 44;
        Scratch.Need(ref sc.Level, n);
        byte[] level = sc.Level;
        Span<int> levelCount = stackalloc int[Levels + 1];
        for (int i = 0; i < n; i++)
        {
            ref Seg s = ref segs[i];
            level[i] = (byte)LevelOf(Math.Max(Math.Abs(s.X1 - s.X0), s.Y1 - s.Y0));
            levelCount[level[i]]++;
        }

        // Typical size: the median level. Longer segments are stored at most two levels above it, or finer if that would
        // spread them over more than ~64 rows of cells.
        int median = MinLevel, acc = 0;
        for (int l = 0; l <= Levels; l++)
        {
            acc += levelCount[l];
            if (acc * 2 >= n)
            {
                median = Math.Max(l, MinLevel);
                break;
            }
        }

        levelCount.Clear();
        int cap = median + 2;
        for (int i = 0; i < n; i++)
        {
            if (level[i] > cap)
            {
                ref Seg s = ref segs[i];
                long len = Math.Max(Math.Abs(s.X1 - s.X0), s.Y1 - s.Y0);
                level[i] = (byte)Math.Max(cap, LevelOf((len >> 6) + 1));
            }

            levelCount[level[i]]++;
        }

        Span<int> used = stackalloc int[Levels + 1];
        int usedCount = 0;
        for (int l = 0; l <= Levels; l++)
        {
            if (levelCount[l] != 0)
            {
                used[usedCount++] = l;
            }
        }

        Span<long> rowY = stackalloc long[MaxRows];
        Span<long> rowC0 = stackalloc long[MaxRows];
        Span<long> rowC1 = stackalloc long[MaxRows];

        // Entries: (cell, segment) pairs, then a chained hash table over them.
        Scratch.Need(ref sc.Ecx, Math.Max(64, n * 2));
        Scratch.Need(ref sc.Ecy, sc.Ecx.Length);
        Scratch.Need(ref sc.Eid, sc.Ecx.Length);
        Scratch.Need(ref sc.Elv, sc.Ecx.Length);
        int entries = 0;
        for (int i = 0; i < n; i++)
        {
            int l = level[i];
            int rows = Rows(in segs[i], l, rowY, rowC0, rowC1);
            for (int r = 0; r < rows; r++)
            {
                for (long cx = rowC0[r]; cx <= rowC1[r]; cx++)
                {
                    if (entries == sc.Ecx.Length)
                    {
                        if (entries >= MaxGridEntries)
                        {
                            throw new ImageLimitException("The boolean operation needs too large a spatial index.");
                        }

                        Scratch.Grow(ref sc.Ecx, entries);
                        Scratch.Grow(ref sc.Ecy, entries);
                        Scratch.Grow(ref sc.Eid, entries);
                        Scratch.Grow(ref sc.Elv, entries);
                    }

                    sc.Ecx[entries] = (int)cx;
                    sc.Ecy[entries] = (int)rowY[r];
                    sc.Eid[entries] = i;
                    sc.Elv[entries] = (byte)l;
                    entries++;
                }
            }
        }

        int[] ecx = sc.Ecx, ecy = sc.Ecy, eid = sc.Eid;
        byte[] elv = sc.Elv;
        int tableSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, entries * 2));
        Scratch.Need(ref sc.Head, tableSize);
        Scratch.Need(ref sc.Next, entries);
        int[] head = sc.Head, next = sc.Next;
        Array.Fill(head, -1, 0, tableSize);
        for (int e = 0; e < entries; e++)
        {
            uint h = CellHash(ecx[e], ecy[e], elv[e]) & (uint)(tableSize - 1);
            next[e] = head[h];
            head[h] = e;
        }

        Scratch.Need(ref sc.Seen, n);
        int[] seen = sc.Seen;
        Array.Clear(seen, 0, n);
        long tests = 0;
        for (int i = 0; i < n; i++)
        {
            ref Seg a = ref segs[i];
            long aMinX = Math.Min(a.X0, a.X1), aMaxX = Math.Max(a.X0, a.X1);
            for (int u = 0; u < usedCount; u++)
            {
                int l = used[u];
                if (l < level[i])
                {
                    continue;
                }

                int rows = Rows(in a, l, rowY, rowC0, rowC1);
                for (int r = 0; r < rows; r++)
                {
                    for (long cx = rowC0[r]; cx <= rowC1[r]; cx++)
                    {
                        uint h = CellHash(cx, rowY[r], l) & (uint)(tableSize - 1);
                        for (int e = head[h]; e >= 0; e = next[e])
                        {
                            if (ecx[e] != (int)cx || ecy[e] != (int)rowY[r] || elv[e] != l)
                            {
                                continue;
                            }

                            int j = eid[e];
                            if (j == i || (l == level[i] && j < i) || seen[j] == i + 1)
                            {
                                continue;
                            }

                            seen[j] = i + 1;
                            if (!a.Dirty && !segs[j].Dirty)
                            {
                                continue;
                            }

                            ref Seg b = ref segs[j];
                            long bMinX = Math.Min(b.X0, b.X1), bMaxX = Math.Max(b.X0, b.X1);
                            if (bMaxX < aMinX || bMinX > aMaxX || b.Y1 < a.Y0 || b.Y0 > a.Y1)
                            {
                                continue;
                            }

                            if (++tests > MaxPairTests)
                            {
                                throw new ImageLimitException("The boolean operation needs too many segment comparisons.");
                            }

                            TestPair(i, ref a, j, ref b, splits);
                        }
                    }
                }
            }
        }
    }

    private static void TestPair(int i, ref Seg a, int j, ref Seg b, SplitList splits)
    {
        long adx = a.X1 - a.X0, ady = a.Y1 - a.Y0, bdx = b.X1 - b.X0, bdy = b.Y1 - b.Y0;
        int o1 = Cross(adx, ady, b.X0 - a.X0, b.Y0 - a.Y0);
        int o2 = Cross(adx, ady, b.X1 - a.X0, b.Y1 - a.Y0);
        if (o1 == o2 && o1 != 0)
        {
            return; // b lies wholly on one side of a's line
        }

        if (o1 == 0 && o2 == 0)
        {
            splits.Collinear = true;
        }

        int o3 = Cross(bdx, bdy, a.X0 - b.X0, a.Y0 - b.Y0);
        int o4 = Cross(bdx, bdy, a.X1 - b.X0, a.Y1 - b.Y0);
        if (o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0 && o1 != o2 && o3 != o4)
        {
            // Proper crossing: p + t·d1 with t = cross(r − p, d2) / cross(d1, d2); rounded to the grid.
            Int128 denom = CrossValue(adx, ady, bdx, bdy);
            Int128 num = CrossValue(b.X0 - a.X0, b.Y0 - a.Y0, bdx, bdy);
            double t = (double)num / (double)denom;
            long px = (long)Math.Round(a.X0 + (t * adx));
            long py = (long)Math.Round(a.Y0 + (t * ady));
            px = Math.Clamp(px, Math.Max(Math.Min(a.X0, a.X1), Math.Min(b.X0, b.X1)), Math.Min(Math.Max(a.X0, a.X1), Math.Max(b.X0, b.X1)));
            py = Math.Clamp(py, Math.Max(a.Y0, b.Y0), Math.Min(a.Y1, b.Y1));
            if (!(px == a.X0 && py == a.Y0) && !(px == a.X1 && py == a.Y1))
            {
                splits.Add(i, px, py, true);
            }

            if (!(px == b.X0 && py == b.Y0) && !(px == b.X1 && py == b.Y1))
            {
                splits.Add(j, px, py, true);
            }

            return;
        }

        // End points lying on the other segment (T junctions, collinear overlaps).
        if (o1 == 0 && Inside(in a, b.X0, b.Y0))
        {
            splits.Add(i, b.X0, b.Y0, false);
        }

        if (o2 == 0 && Inside(in a, b.X1, b.Y1))
        {
            splits.Add(i, b.X1, b.Y1, false);
        }

        if (o3 == 0 && Inside(in b, a.X0, a.Y0))
        {
            splits.Add(j, a.X0, a.Y0, false);
        }

        if (o4 == 0 && Inside(in b, a.X1, a.Y1))
        {
            splits.Add(j, a.X1, a.Y1, false);
        }
    }

    // A point already known to be collinear with s: strictly between its end points?
    private static bool Inside(in Seg s, long x, long y)
    {
        if ((x == s.X0 && y == s.Y0) || (x == s.X1 && y == s.Y1))
        {
            return false;
        }

        return x >= Math.Min(s.X0, s.X1) && x <= Math.Max(s.X0, s.X1) && y >= s.Y0 && y <= s.Y1;
    }

    // ---- Sweep ----

    // Left-to-right order of two segments that both span the strip whose doubled middle height is y2. The position along the
    // strip is first compared in doubles; only when the two are closer than the doubles can tell is the exact value used.
    private static int CompareAt(in Seg a, double slopeA, in Seg b, double slopeB, long y2)
    {
        double xa = (2.0 * a.X0) + ((y2 - (2.0 * a.Y0)) * slopeA);
        double xb = (2.0 * b.X0) + ((y2 - (2.0 * b.Y0)) * slopeB);
        double diff = xa - xb;
        if (Math.Abs(diff) > 1e-3 + (1e-12 * (Math.Abs(xa) + Math.Abs(xb))))
        {
            return diff < 0 ? -1 : 1;
        }

        Int128 na = ((Int128)(2 * a.X0) * (a.Y1 - a.Y0)) + ((Int128)(y2 - (2 * a.Y0)) * (a.X1 - a.X0));
        Int128 nb = ((Int128)(2 * b.X0) * (b.Y1 - b.Y0)) + ((Int128)(y2 - (2 * b.Y0)) * (b.X1 - b.X0));
        return (na * (b.Y1 - b.Y0)).CompareTo(nb * (a.Y1 - a.Y0));
    }

    // A pair of neighbors that the sweep cannot accept: identical (to be merged) or in need of splitting.
    private static bool Bad(in Seg a, in Seg b, ref bool duplicate)
    {
        if (a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1)
        {
            duplicate = true;
            return true;
        }

        // Pairs of segments that did not move were compared with everything already.
        return (a.Dirty || b.Dirty) && Conflict(in a, in b);
    }

    // Whether two segments cross, or one has an end point strictly inside the other (the cases that need splitting).
    private static bool Conflict(in Seg a, in Seg b)
    {
        long aMinX = Math.Min(a.X0, a.X1), aMaxX = Math.Max(a.X0, a.X1), bMinX = Math.Min(b.X0, b.X1), bMaxX = Math.Max(b.X0, b.X1);
        if (bMaxX < aMinX || bMinX > aMaxX || b.Y1 < a.Y0 || b.Y0 > a.Y1)
        {
            return false;
        }

        long adx = a.X1 - a.X0, ady = a.Y1 - a.Y0, bdx = b.X1 - b.X0, bdy = b.Y1 - b.Y0;
        int o1 = Cross(adx, ady, b.X0 - a.X0, b.Y0 - a.Y0);
        int o2 = Cross(adx, ady, b.X1 - a.X0, b.Y1 - a.Y0);
        if (o1 == o2 && o1 != 0)
        {
            return false;
        }

        int o3 = Cross(bdx, bdy, a.X0 - b.X0, a.Y0 - b.Y0);
        int o4 = Cross(bdx, bdy, a.X1 - b.X0, a.Y1 - b.Y0);
        if (o3 == o4 && o3 != 0)
        {
            return false;
        }

        if (o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0)
        {
            return true; // opposite sides both ways: a proper crossing
        }

        return (o1 == 0 && Inside(in a, b.X0, b.Y0)) || (o2 == 0 && Inside(in a, b.X1, b.Y1))
            || (o3 == 0 && Inside(in b, a.X0, a.Y0)) || (o4 == 0 && Inside(in b, a.X1, a.Y1));
    }

    private static bool Inside(int w, IntersectionRule rule) => rule == IntersectionRule.NonZero ? w != 0 : (w & 1) != 0;

    private static bool Result(BooleanOperation op, bool s, bool c) => op switch
    {
        BooleanOperation.Union => s || c,
        BooleanOperation.Intersection => s && c,
        BooleanOperation.Difference => s && !c,
        _ => s ^ c,
    };

    /// <summary>
    /// Finds the boundary of the result (into <c>sc.Edges</c>). With <paramref name="verify"/> it also checks every pair of
    /// segments that become neighbors in the sweep for a crossing or a touch and returns false at the first one (see <see cref="Resolve"/>).
    /// </summary>
    private static bool Sweep(Seg[] segs, int n, BooleanOperation op, IntersectionRule sr, IntersectionRule cr, bool verify, Scratch sc, out int count, out bool duplicate)
    {
        duplicate = false;
        count = 0;
        int ns = 0, nh = 0;
        for (int i = 0; i < n; i++)
        {
            if (segs[i].Y0 == segs[i].Y1)
            {
                nh++;
            }
            else
            {
                ns++;
            }
        }

        Scratch.Need(ref sc.ByStart, ns);
        Scratch.Need(ref sc.ByEnd, ns);
        Scratch.Need(ref sc.ByHor, nh);
        Scratch.Need(ref sc.StartKey, ns);
        Scratch.Need(ref sc.EndKey, ns);
        Scratch.Need(ref sc.HorKey, nh);
        int[] byStart = sc.ByStart, byEnd = sc.ByEnd, byHor = sc.ByHor;
        long[] startKey = sc.StartKey, endKey = sc.EndKey, horKey = sc.HorKey;
        int a0 = 0, h0 = 0;
        for (int i = 0; i < n; i++)
        {
            if (segs[i].Y0 == segs[i].Y1)
            {
                byHor[h0] = i;
                horKey[h0++] = segs[i].Y0;
            }
            else
            {
                byStart[a0] = byEnd[a0] = i;
                startKey[a0] = segs[i].Y0;
                endKey[a0++] = segs[i].Y1;
            }
        }

        Array.Sort(startKey, byStart, 0, ns);
        Array.Sort(endKey, byEnd, 0, ns);
        if (verify && nh > 1)
        {
            // Identical horizontal segments are not neighbors in the sweep, so look for them here.
            Seg[] sg = segs;
            Array.Sort(byHor, 0, nh, Comparer<int>.Create((x, y) =>
            {
                int c = sg[x].Y0.CompareTo(sg[y].Y0);
                if (c != 0) { return c; }
                c = sg[x].X0.CompareTo(sg[y].X0);
                return c != 0 ? c : sg[x].X1.CompareTo(sg[y].X1);
            }));
            for (int k = 0; k < nh; k++)
            {
                horKey[k] = segs[byHor[k]].Y0;
                if (k > 0 && segs[byHor[k]].X0 == segs[byHor[k - 1]].X0 && segs[byHor[k]].X1 == segs[byHor[k - 1]].X1 && horKey[k] == horKey[k - 1])
                {
                    duplicate = true;
                    return false;
                }
            }
        }
        else if (nh > 1)
        {
            Array.Sort(horKey, byHor, 0, nh);
        }

        Scratch.Need(ref sc.Slope, n);
        double[] slope = sc.Slope;
        for (int i = 0; i < n; i++)
        {
            if (segs[i].Y1 != segs[i].Y0)
            {
                slope[i] = (double)(segs[i].X1 - segs[i].X0) / (segs[i].Y1 - segs[i].Y0);
            }
        }

        Scratch.Need(ref sc.Edges, Math.Max(16, n / 4));
        int outCount = 0;
        Scratch.Need(ref sc.Active, Math.Max(16, 8));
        int ac = 0;
        Scratch.Need(ref sc.Mark, n);
        int[] mark = sc.Mark;
        Array.Clear(mark, 0, n);
        int stamp = 0;
        int si = 0, ei = 0, hi = 0;
        long prevY = 0;
        while (si < ns || ei < ns || hi < nh)
        {
            long y = long.MaxValue;
            if (si < ns) { y = Math.Min(y, startKey[si]); }
            if (ei < ns) { y = Math.Min(y, endKey[ei]); }
            if (hi < nh) { y = Math.Min(y, horKey[hi]); }
            int[] active = sc.Active;

            // Horizontal segments: the winding above one is read off the strip above this line; below it differs by the segment's own contribution.
            for (; hi < nh && horKey[hi] == y; hi++)
            {
                ref Seg h = ref segs[byHor[hi]];
                int ws = 0, wc = 0;
                long qx2 = h.X0 + h.X1, qy2 = 2 * y;
                int idx = 0;
                for (; idx < ac; idx++)
                {
                    ref Seg s = ref segs[active[idx]];
                    // The point is right of the segment when the cross product is negative (y grows downward).
                    if (Cross(2 * (s.X1 - s.X0), 2 * (s.Y1 - s.Y0), qx2 - (2 * s.X0), qy2 - (2 * s.Y0)) >= 0)
                    {
                        break;
                    }

                    ws += s.Ds;
                    wc += s.Dc;
                }

                if (verify && ((idx > 0 && Bad(in h, in segs[active[idx - 1]], ref duplicate)) || (idx < ac && Bad(in h, in segs[active[idx]], ref duplicate))))
                {
                    return false;
                }

                bool above = Result(op, Inside(ws, sr), Inside(wc, cr));
                bool below = Result(op, Inside(ws - h.Ds, sr), Inside(wc - h.Dc, cr));
                if (above != below)
                {
                    Emit(sc, ref outCount, h.X0, h.Y0, h.X1, h.Y1, !above);
                }
            }

            // Segments that end here leave the active list; its order is that of the strip they just crossed.
            for (; ei < ns && endKey[ei] == y; ei++)
            {
                int id = byEnd[ei];
                long y2 = prevY + y;
                int lo = 0, hiIdx = ac - 1, found = -1;
                while (lo <= hiIdx)
                {
                    int mid = (lo + hiIdx) >> 1;
                    int cmp = active[mid] == id ? 0 : CompareAt(segs[id], slope[id], segs[active[mid]], slope[active[mid]], y2);
                    if (cmp == 0)
                    {
                        found = mid;
                        break;
                    }

                    if (cmp < 0)
                    {
                        hiIdx = mid - 1;
                    }
                    else
                    {
                        lo = mid + 1;
                    }
                }

                if (found < 0 || active[found] != id)
                {
                    found = Array.IndexOf(active, id, 0, ac);
                }

                Array.Copy(active, found + 1, active, found, ac - found - 1);
                ac--;
                if (verify && found > 0 && found < ac && Bad(in segs[active[found - 1]], in segs[active[found]], ref duplicate))
                {
                    return false;
                }
            }

            int sj = si;
            while (sj < ns && startKey[sj] == y)
            {
                sj++;
            }

            if (sj > si)
            {
                long next = long.MaxValue;
                if (sj < ns) { next = Math.Min(next, startKey[sj]); }
                if (ei < ns) { next = Math.Min(next, endKey[ei]); }
                if (hi < nh) { next = Math.Min(next, horKey[hi]); }

                long y2 = y + next;
                stamp++;
                for (int k = si; k < sj; k++)
                {
                    int id = byStart[k];
                    mark[id] = stamp;
                    int lo = 0, hiIdx = ac;
                    while (lo < hiIdx)
                    {
                        int mid = (lo + hiIdx) >> 1;
                        if (CompareAt(segs[id], slope[id], segs[active[mid]], slope[active[mid]], y2) < 0)
                        {
                            hiIdx = mid;
                        }
                        else
                        {
                            lo = mid + 1;
                        }
                    }

                    if (ac == active.Length)
                    {
                        Scratch.Grow(ref sc.Active, ac);
                        active = sc.Active;
                    }

                    Array.Copy(active, lo, active, lo + 1, ac - lo);
                    active[lo] = id;
                    ac++;
                    if (verify && ((lo > 0 && Bad(in segs[id], in segs[active[lo - 1]], ref duplicate)) || (lo + 1 < ac && Bad(in segs[id], in segs[active[lo + 1]], ref duplicate))))
                    {
                        return false;
                    }
                }

                int ws = 0, wc = 0;
                for (int p = 0; p < ac; p++)
                {
                    int e = active[p];
                    ref Seg s = ref segs[e];
                    if (mark[e] == stamp)
                    {
                        bool before = Result(op, Inside(ws, sr), Inside(wc, cr));
                        bool after = Result(op, Inside(ws + s.Ds, sr), Inside(wc + s.Dc, cr));
                        if (before != after)
                        {
                            Emit(sc, ref outCount, s.X0, s.Y0, s.X1, s.Y1, !after);
                        }
                    }

                    ws += s.Ds;
                    wc += s.Dc;
                }

                si = sj;
            }

            prevY = y;
        }

        count = outCount;
        return true;
    }

    // The interior stays on the left of every emitted edge (as seen on screen), so outer contours and holes come out opposite.
    private static void Emit(Scratch sc, ref int count, long x0, long y0, long x1, long y1, bool reverse)
    {
        if (count == sc.Edges.Length)
        {
            Scratch.Grow(ref sc.Edges, count);
        }

        sc.Edges[count++] = reverse ? new BEdge { Fx = x1, Fy = y1, Tx = x0, Ty = y0 } : new BEdge { Fx = x0, Fy = y0, Tx = x1, Ty = y1 };
    }

    // ---- Contours ----

    private static Path Assemble(Scratch sc, int m)
    {
        if (m == 0)
        {
            return Path.Empty;
        }

        BEdge[] edges = sc.Edges;
        Scratch.Need(ref sc.Order, m);
        Scratch.Need(ref sc.Link, m);
        int[] next = sc.Order, chain = sc.Link;

        // Edges by their start vertex, in a chained hash table.
        int tableSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, m * 2));
        Scratch.Need(ref sc.Head, tableSize);
        int[] head = sc.Head;
        Array.Fill(head, -1, 0, tableSize);
        for (int e = 0; e < m; e++)
        {
            uint h = CellHash(edges[e].Fx, edges[e].Fy, 0) & (uint)(tableSize - 1);
            chain[e] = head[h];
            head[h] = e;
        }

        for (int e = 0; e < m; e++)
        {
            long tx = edges[e].Tx, ty = edges[e].Ty;
            int best = -1;
            long dx = tx - edges[e].Fx, dy = ty - edges[e].Fy;
            for (int cand = head[CellHash(tx, ty, 0) & (uint)(tableSize - 1)]; cand >= 0; cand = chain[cand])
            {
                if (edges[cand].Fx != tx || edges[cand].Fy != ty)
                {
                    continue;
                }

                if (best < 0 || Sharper(dx, dy, edges[cand], edges[best]))
                {
                    best = cand;
                }
            }

            next[e] = best;
        }

        var verbs = new List<byte>(m + 8);
        var coords = new List<float>((2 * m) + 8);
        Scratch.Need(ref sc.SeenEdge, m);
        bool[] seen = sc.SeenEdge;
        Array.Clear(seen, 0, m);
        var pts = new List<(long X, long Y)>();
        var keep = new List<(long X, long Y)>();
        for (int first = 0; first < m; first++)
        {
            if (seen[first])
            {
                continue;
            }

            pts.Clear();
            int e = first;
            while (e >= 0 && !seen[e])
            {
                seen[e] = true;
                pts.Add((edges[e].Fx, edges[e].Fy));
                e = next[e];
            }

            Simplify(pts, keep);
            if (pts.Count < 3)
            {
                continue;
            }

            verbs.Add((byte)Verb.Move);
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    verbs.Add((byte)Verb.Line);
                }

                coords.Add((float)(pts[i].X / (double)Scale));
                coords.Add((float)(pts[i].Y / (double)Scale));
            }

            verbs.Add((byte)Verb.Close);
        }

        return verbs.Count == 0 ? Path.Empty : new Path(verbs.ToArray(), coords.ToArray());
    }

    // True when edge a leaves the vertex at a sharper left turn than edge b, after arriving along (dx, dy).
    private static bool Sharper(long dx, long dy, in BEdge a, in BEdge b)
    {
        long ax = a.Tx - a.Fx, ay = a.Ty - a.Fy, bx = b.Tx - b.Fx, by = b.Ty - b.Fy;
        int ra = Rank(dx, dy, ax, ay), rb = Rank(dx, dy, bx, by);
        if (ra != rb)
        {
            return ra > rb;
        }

        return Cross(ax, ay, bx, by) > 0;
    }

    private static int Rank(long dx, long dy, long wx, long wy)
    {
        // On screen (y down) a left turn has a negative cross product.
        int c = -Cross(dx, dy, wx, wy);
        if (c > 0) { return 2; }
        if (c < 0) { return 0; }
        return Dot(dx, dy, wx, wy) < 0 ? 3 : 1;
    }

    // Removes vertices in the middle of a straight run (cyclically): a vertex whose neighbors are collinear with it, in the same direction.
    private static void Simplify(List<(long X, long Y)> pts, List<(long X, long Y)> keep)
    {
        int n = pts.Count;
        if (n < 3)
        {
            return;
        }

        keep.Clear();
        for (int i = 0; i < n; i++)
        {
            var p = pts[(i + n - 1) % n];
            var c = pts[i];
            var q = pts[(i + 1) % n];
            if (Cross(c.X - p.X, c.Y - p.Y, q.X - c.X, q.Y - c.Y) == 0 && Dot(c.X - p.X, c.Y - p.Y, q.X - c.X, q.Y - c.Y) > 0)
            {
                continue;
            }

            keep.Add(c);
        }

        if (keep.Count != n)
        {
            pts.Clear();
            pts.AddRange(keep);
        }
    }
}
