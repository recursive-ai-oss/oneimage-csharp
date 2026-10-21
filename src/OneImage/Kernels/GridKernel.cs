using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// A regular n-dimensional lookup grid with 1, 3 or 4 inputs (all in 0..1) and up to 4 outputs. Entries are stored as four floats
/// (outputs padded with zero) so one vector load fetches an entry. The first input varies slowest, as in ICC tables.
/// </summary>
internal sealed class Grid
{
    public Grid(int inputs, int points, int outputs)
        : this(inputs, MakeDims(inputs, points), outputs)
    {
    }

    public Grid(int inputs, int[] dims, int outputs)
    {
        if (inputs is not (1 or 3 or 4) || dims.Length != inputs || outputs is < 1 or > 4)
        {
            throw new ImageFormatException("Unsupported lookup table shape.");
        }

        long entries = 1;
        foreach (int d in dims)
        {
            if (d < 2)
            {
                throw new ImageFormatException("Lookup table grid needs at least two points per axis.");
            }

            entries = checked(entries * d);
            if (entries > (1 << 24))
            {
                throw new ImageLimitException("Lookup table is too large.");
            }
        }

        Inputs = inputs;
        Dims = dims;
        Outputs = outputs;
        Entries = (int)entries;
        Data = new float[Entries * 4];
    }

    public int Inputs { get; }

    public int[] Dims { get; }

    public int Outputs { get; }

    public int Entries { get; }

    public float[] Data { get; }

    private static int[] MakeDims(int inputs, int points)
    {
        var d = new int[inputs];
        Array.Fill(d, points);
        return d;
    }

    /// <summary>Returns the entry index of the grid node given per-axis node indices.</summary>
    public int Index(ReadOnlySpan<int> node)
    {
        int idx = 0;
        for (int i = 0; i < Inputs; i++)
        {
            idx = (idx * Dims[i]) + node[i];
        }

        return idx;
    }
}

internal static class GridKernel
{
    /// <summary>
    /// Interpolates <paramref name="count"/> pixels. Inputs are clamped to 0..1. Three inputs use tetrahedral interpolation;
    /// four inputs interpolate the first axis linearly between two tetrahedral lookups over the other three; one input is linear.
    /// All arithmetic is plain IEEE float multiply/add in a fixed order, so results are identical on every hardware path.
    /// </summary>
    public static void Eval(Grid grid, ReadOnlySpan<float> src, Span<float> dst, int count)
    {
        int ins = grid.Inputs;
        int outs = grid.Outputs;
        if (src.Length < (long)count * ins || dst.Length < (long)count * outs)
        {
            throw new ArgumentException("Buffers are too small for the pixel count.");
        }

        ref float data = ref MemoryMarshal.GetArrayDataReference(grid.Data);
        Span<float> tmp = stackalloc float[4];
        for (int p = 0; p < count; p++)
        {
            Vector128<float> v;
            switch (ins)
            {
                case 3:
                    v = Tetra(grid, ref data, 0, src[p * 3], src[(p * 3) + 1], src[(p * 3) + 2]);
                    break;
                case 4:
                    v = Eval4(grid, ref data, src[p * 4], src[(p * 4) + 1], src[(p * 4) + 2], src[(p * 4) + 3]);
                    break;
                default:
                    v = Eval1(grid, ref data, src[p]);
                    break;
            }

            if (outs == 4)
            {
                v.CopyTo(dst.Slice(p * 4, 4));
            }
            else
            {
                v.CopyTo(tmp);
                for (int k = 0; k < outs; k++)
                {
                    dst[(p * outs) + k] = tmp[k];
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Locate(float x, int n, out int i, out float f)
    {
        float pos = !(x > 0f) ? 0f : x >= 1f ? 1f : x;
        pos *= n - 1;
        i = (int)pos;
        if (i >= n - 1)
        {
            i = n - 2;
        }

        f = pos - i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Load(ref float data, int entry) =>
        Vector128.LoadUnsafe(ref data, (nuint)entry * 4);

    private static Vector128<float> Eval1(Grid g, ref float data, float x)
    {
        Locate(x, g.Dims[0], out int i, out float f);
        Vector128<float> a = Load(ref data, i);
        Vector128<float> b = Load(ref data, i + 1);
        return a + ((b - a) * Vector128.Create(f));
    }

    private static Vector128<float> Eval4(Grid g, ref float data, float c, float m, float y, float k)
    {
        Locate(c, g.Dims[0], out int i, out float f);
        int stride = g.Dims[1] * g.Dims[2] * g.Dims[3];
        Vector128<float> a = Tetra(g, ref data, i * stride, m, y, k);
        Vector128<float> b = Tetra(g, ref data, (i + 1) * stride, m, y, k);
        return a + ((b - a) * Vector128.Create(f));
    }

    /// <summary>Tetrahedral interpolation over the last three axes of the grid, starting at entry <paramref name="baseEntry"/>.</summary>
    private static Vector128<float> Tetra(Grid g, ref float data, int baseEntry, float x, float y, float z)
    {
        int[] dims = g.Dims;
        int d1 = dims[dims.Length - 3];
        int d2 = dims[dims.Length - 2];
        int d3 = dims[dims.Length - 1];
        Locate(x, d1, out int ix, out float fx);
        Locate(y, d2, out int iy, out float fy);
        Locate(z, d3, out int iz, out float fz);
        int sx = d2 * d3;
        int sy = d3;
        int o = baseEntry + (ix * sx) + (iy * sy) + iz;
        Vector128<float> c000 = Load(ref data, o);
        Vector128<float> c111 = Load(ref data, o + sx + sy + 1);
        Vector128<float> r;
        if (fx >= fy)
        {
            if (fy >= fz)
            {
                Vector128<float> c100 = Load(ref data, o + sx);
                Vector128<float> c110 = Load(ref data, o + sx + sy);
                r = c000 + ((c100 - c000) * Vector128.Create(fx));
                r += (c110 - c100) * Vector128.Create(fy);
                r += (c111 - c110) * Vector128.Create(fz);
            }
            else if (fx >= fz)
            {
                Vector128<float> c100 = Load(ref data, o + sx);
                Vector128<float> c101 = Load(ref data, o + sx + 1);
                r = c000 + ((c100 - c000) * Vector128.Create(fx));
                r += (c101 - c100) * Vector128.Create(fz);
                r += (c111 - c101) * Vector128.Create(fy);
            }
            else
            {
                Vector128<float> c001 = Load(ref data, o + 1);
                Vector128<float> c101 = Load(ref data, o + sx + 1);
                r = c000 + ((c001 - c000) * Vector128.Create(fz));
                r += (c101 - c001) * Vector128.Create(fx);
                r += (c111 - c101) * Vector128.Create(fy);
            }
        }
        else if (fz >= fy)
        {
            Vector128<float> c001 = Load(ref data, o + 1);
            Vector128<float> c011 = Load(ref data, o + sy + 1);
            r = c000 + ((c001 - c000) * Vector128.Create(fz));
            r += (c011 - c001) * Vector128.Create(fy);
            r += (c111 - c011) * Vector128.Create(fx);
        }
        else if (fz >= fx)
        {
            Vector128<float> c010 = Load(ref data, o + sy);
            Vector128<float> c011 = Load(ref data, o + sy + 1);
            r = c000 + ((c010 - c000) * Vector128.Create(fy));
            r += (c011 - c010) * Vector128.Create(fz);
            r += (c111 - c011) * Vector128.Create(fx);
        }
        else
        {
            Vector128<float> c010 = Load(ref data, o + sy);
            Vector128<float> c110 = Load(ref data, o + sx + sy);
            r = c000 + ((c010 - c000) * Vector128.Create(fy));
            r += (c110 - c010) * Vector128.Create(fx);
            r += (c111 - c110) * Vector128.Create(fz);
        }

        return r;
    }
}
