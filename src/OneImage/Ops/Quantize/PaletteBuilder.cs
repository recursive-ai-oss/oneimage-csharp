using System.Buffers.Binary;

namespace OneImage.Ops.Quantize;

/// <summary>
/// Builds a palette from rows of RGBA8 with the chosen <see cref="Quantizer"/>: feed rows with <see cref="AddRows"/>, then call
/// <see cref="Build"/>. The Wu quantizer handles alpha as <see cref="ColorQuantizer"/> does; the others treat alpha as a switch
/// (below the threshold a pixel is transparent and gets its own palette entry).
/// </summary>
internal sealed class PaletteBuilder
{
    private readonly Quantizer _kind;
    private readonly int _maxColors;
    private readonly int _threshold;
    private readonly bool _reserveTransparent;
    private readonly ColorQuantizer? _wu;
    private readonly long[]? _count;
    private readonly long[]? _sumR, _sumG, _sumB;
    private long _transparent;

    public PaletteBuilder(Quantizer kind, int maxColors, QuantizeAlpha alpha, int alphaThreshold = 128, bool reserveTransparent = false)
    {
        _kind = kind;
        _maxColors = maxColors;
        _threshold = alphaThreshold;
        _reserveTransparent = reserveTransparent;
        switch (kind)
        {
            case Quantizer.Wu:
                _wu = new ColorQuantizer(maxColors, alpha, alphaThreshold, reserveTransparent);
                break;
            case Quantizer.Octree:
                _count = new long[Octree.Bins];
                _sumR = new long[Octree.Bins];
                _sumG = new long[Octree.Bins];
                _sumB = new long[Octree.Bins];
                break;
        }
    }

    public void AddRows(ReadOnlySpan<byte> rgba)
    {
        if (_wu != null)
        {
            _wu.AddRows(rgba);
            return;
        }

        int n = rgba.Length >> 2;
        if (_count is null)
        {
            // Fixed palettes only need to know whether anything is transparent.
            if (_transparent == 0 && Kernels.QuantizeKernels.HasAlphaBelow(rgba.Slice(0, n * 4), (byte)_threshold))
            {
                _transparent = 1;
            }

            return;
        }

        for (int i = 0; i < n; i++)
        {
            int r = rgba[i * 4];
            int g = rgba[(i * 4) + 1];
            int b = rgba[(i * 4) + 2];
            if (rgba[(i * 4) + 3] < _threshold)
            {
                _transparent++;
                continue;
            }

            int bin = Octree.Bin(r, g, b);
            _count[bin]++;
            _sumR![bin] += r;
            _sumG![bin] += g;
            _sumB![bin] += b;
        }
    }

    public QuantizedPalette Build()
    {
        if (_wu != null)
        {
            return _wu.BuildPalette();
        }

        bool needT = _transparent > 0 || _reserveTransparent;
        byte[] colors;
        if (_kind == Quantizer.Octree)
        {
            colors = Octree.Reduce(_count!, _sumR!, _sumG!, _sumB!, Math.Max(1, _maxColors - (needT ? 1 : 0)));
        }
        else
        {
            IReadOnlyList<Rgba32> fixedColors = _kind == Quantizer.WebSafe ? KnownPalettes.WebSafe : KnownPalettes.Werner;
            colors = new byte[fixedColors.Count * 4];
            for (int i = 0; i < fixedColors.Count; i++)
            {
                Rgba32 c = fixedColors[i];
                colors[i * 4] = c.R;
                colors[(i * 4) + 1] = c.G;
                colors[(i * 4) + 2] = c.B;
                colors[(i * 4) + 3] = 255;
            }
        }

        int n = colors.Length / 4;
        int tIndex = -1;
        if (needT)
        {
            Array.Resize(ref colors, (n + 1) * 4);
            tIndex = n++;
        }

        return new QuantizedPalette(colors, n, tIndex, QuantizeAlpha.Binary, _threshold, null);
    }
}

/// <summary>
/// An octree over 5 bits per channel (32768 cells, five levels below the root). Cells are merged into their parent, smallest pixel
/// count first, until the number of leaves fits; each leaf becomes the mean color of the pixels under it. Ties are broken by cell
/// number, so the palette depends only on the pixel counts.
/// </summary>
internal static class Octree
{
    public const int Bins = 1 << 15;
    private const int Levels = 5;

    // Morton order: at each level the three channel bits are interleaved, so a cell's parent is its index shifted right by 3.
    public static int Bin(int r, int g, int b)
    {
        r >>= 3;
        g >>= 3;
        b >>= 3;
        int m = 0;
        for (int i = Levels - 1; i >= 0; i--)
        {
            m = (m << 3) | (((r >> i) & 1) << 2) | (((g >> i) & 1) << 1) | ((b >> i) & 1);
        }

        return m;
    }

    // Node numbering: level l holds 8^l nodes starting at (8^l - 1) / 7; level 5 are the bins.
    private static int First(int level) => ((1 << (3 * level)) - 1) / 7;

    public static byte[] Reduce(long[] count, long[] sumR, long[] sumG, long[] sumB, int target)
    {
        int total = First(Levels + 1);
        var cnt = new long[total];
        var sr = new long[total];
        var sg = new long[total];
        var sb = new long[total];
        var reps = new int[total];       // leaves under the node
        var internalKids = new int[total]; // non-empty children that are not leaves yet
        var collapsed = new bool[total];
        int leaves = 0;
        for (int i = 0; i < Bins; i++)
        {
            if (count[i] == 0)
            {
                continue;
            }

            leaves++;
            for (int l = Levels, p = i; l >= 0; l--, p >>= 3)
            {
                int id = First(l) + p;
                cnt[id] += count[i];
                sr[id] += sumR[i];
                sg[id] += sumG[i];
                sb[id] += sumB[i];
                reps[id]++;
            }
        }

        // internalKids of a node at level l < 4: its non-empty children that are internal nodes (level <= 4).
        for (int l = 1; l < Levels; l++)
        {
            for (int p = 0; p < (1 << (3 * l)); p++)
            {
                if (cnt[First(l) + p] > 0)
                {
                    internalKids[First(l - 1) + (p >> 3)]++;
                }
            }
        }

        var queue = new PriorityQueue<int, (long Count, int Id)>();
        for (int p = 0; p < (1 << (3 * (Levels - 1))); p++)
        {
            int id = First(Levels - 1) + p;
            if (cnt[id] > 0)
            {
                queue.Enqueue(id, (cnt[id], id));
            }
        }

        while (leaves > target && queue.TryDequeue(out int node, out _))
        {
            int level = LevelOf(node);
            int removed = reps[node] - 1;
            leaves -= removed;
            collapsed[node] = true;
            // Every ancestor now holds fewer leaves.
            int pid = node;
            for (int l = level; l > 0; l--)
            {
                pid = First(l - 1) + ((pid - First(l)) >> 3);
                reps[pid] -= removed;
            }

            reps[node] = 1;
            if (level > 0)
            {
                int parent = First(level - 1) + ((node - First(level)) >> 3);
                if (--internalKids[parent] == 0 && !collapsed[parent])
                {
                    queue.Enqueue(parent, (cnt[parent], parent));
                }
            }
        }

        // Collect the leaves: walk down from the root until a collapsed node or a bin.
        var found = new List<(long Count, int Id)>();
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.TryPop(out int id))
        {
            int level = LevelOf(id);
            if (collapsed[id] || level == Levels)
            {
                found.Add((cnt[id], id));
                continue;
            }

            int child0 = First(level + 1) + ((id - First(level)) << 3);
            for (int c = 7; c >= 0; c--)
            {
                if (cnt[child0 + c] > 0)
                {
                    stack.Push(child0 + c);
                }
            }
        }

        if (found.Count == 0)
        {
            return [];
        }

        // Most used colors first; the node number settles ties.
        found.Sort(static (a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : a.Id.CompareTo(b.Id));
        var rgba = new byte[found.Count * 4];
        for (int i = 0; i < found.Count; i++)
        {
            int id = found[i].Id;
            long n = cnt[id];
            rgba[i * 4] = (byte)((sr[id] + (n / 2)) / n);
            rgba[(i * 4) + 1] = (byte)((sg[id] + (n / 2)) / n);
            rgba[(i * 4) + 2] = (byte)((sb[id] + (n / 2)) / n);
            rgba[(i * 4) + 3] = 255;
        }

        return rgba;
    }

    private static int LevelOf(int id)
    {
        int l = 0;
        while (id >= First(l + 1))
        {
            l++;
        }

        return l;
    }
}
