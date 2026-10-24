using System.Buffers;
using System.Numerics;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Separable complex kernels that approximate a disc (a lens aperture). Each component k is a one-dimensional complex kernel
/// <c>K_k(n) = exp(-a_k u) (cos(b_k u) + i sin(b_k u))</c> with <c>u = (n s / r)²</c> for n = -r..r (s is a per-count scale), and the
/// two-dimensional kernel is <c>sum_k A_k Re(K_k(x) K_k(y)) + B_k Im(K_k(x) K_k(y))</c>, normalized to sum 1. Because the
/// product of complex numbers splits into real products, each component costs four real 1-D passes. The parameters (a, b, A, B)
/// are the published fits of Garcia, "Circular Separable Convolution Depth of Field" (2017), for 1 to 6 components.
/// </summary>
internal sealed class BokehKernels
{
    private static readonly float[] s_scales = [1.4f, 1.2f, 1.2f, 1.2f, 1.2f, 1.2f];

    // a, b, A, B per component.
    private static readonly float[][][] s_params =
    [
        [[0.862325f, 1.624835f, 0.767583f, 1.862321f]],
        [[0.886528f, 5.268909f, 0.411259f, -0.548794f], [1.960518f, 1.558213f, 0.513282f, 4.56111f]],
        [[2.17649f, 5.043495f, 1.621035f, -2.105439f], [1.019306f, 9.027613f, -0.28086f, -0.162882f], [2.81511f, 1.597273f, -0.366471f, 10.300301f]],
        [[4.338459f, 1.553635f, -5.767909f, 46.164397f], [3.839993f, 4.693183f, 9.795391f, -15.227561f], [2.79188f, 8.178137f, -3.048324f, 0.302959f], [1.34219f, 12.328289f, 0.010001f, 0.24465f]],
        [[4.892608f, 1.685979f, -22.356787f, 85.91246f], [4.71187f, 4.998496f, 35.918936f, -28.875618f], [4.052795f, 8.244168f, -13.212253f, -1.578428f], [2.929212f, 11.900859f, 0.507991f, 1.816328f], [1.512961f, 16.116382f, 0.138051f, -0.01f]],
        [[5.143778f, 2.079813f, -82.326596f, 111.231024f], [5.612426f, 6.153387f, 113.878661f, 58.004879f], [5.982921f, 9.802895f, 39.479083f, -162.028887f], [6.505167f, 11.059237f, -71.286026f, 95.027069f], [3.869579f, 14.81052f, 1.405746f, -3.704914f], [2.201904f, 19.032909f, -0.152784f, -0.107988f]],
    ];

    public BokehKernels(int radius, int components)
    {
        Radius = radius;
        Components = components;
        float[][] ps = s_params[components - 1];
        float scale = s_scales[components - 1];
        int size = (2 * radius) + 1;
        var re = new double[components][];
        var im = new double[components][];
        for (int k = 0; k < components; k++)
        {
            re[k] = new double[size];
            im[k] = new double[size];
            for (int i = 0; i < size; i++)
            {
                double n = i - radius;
                double u = n * scale / radius;
                u *= u;
                double e = Math.Exp(-ps[k][0] * u);
                re[k][i] = e * Math.Cos(ps[k][1] * u);
                im[k][i] = e * Math.Sin(ps[k][1] * u);
            }
        }

        // The sum of the two-dimensional kernel is sum_k A Re(S²) + B Im(S²) where S is the sum of K_k; scale every K_k by
        // 1/sqrt(total) so that it is 1.
        double total = 0;
        for (int k = 0; k < components; k++)
        {
            double sr = 0;
            double si = 0;
            for (int i = 0; i < size; i++)
            {
                sr += re[k][i];
                si += im[k][i];
            }

            total += (ps[k][2] * ((sr * sr) - (si * si))) + (ps[k][3] * 2 * sr * si);
        }

        double norm = 1.0 / Math.Sqrt(total);
        HalfRe = new float[components][];
        HalfIm = new float[components][];
        HalfA = new float[components][];
        HalfB = new float[components][];
        CenterRe = new float[components];
        CenterIm = new float[components];
        CenterA = new float[components];
        CenterB = new float[components];
        for (int k = 0; k < components; k++)
        {
            double a = ps[k][2];
            double b = ps[k][3];
            HalfRe[k] = new float[radius];
            HalfIm[k] = new float[radius];
            HalfA[k] = new float[radius];
            HalfB[k] = new float[radius];
            for (int t = 0; t <= radius; t++)
            {
                double kr = re[k][radius + t] * norm;
                double ki = im[k][radius + t] * norm;

                // Vertical pass of component k: A Re(Z) + B Im(Z) with Z = sum K(y) h(y), h complex, splits into one real
                // kernel on the real part and one on the imaginary part of the horizontal result.
                float va = (float)((a * kr) + (b * ki));
                float vb = (float)((b * kr) - (a * ki));
                if (t == 0)
                {
                    CenterRe[k] = (float)kr;
                    CenterIm[k] = (float)ki;
                    CenterA[k] = va;
                    CenterB[k] = vb;
                }
                else
                {
                    HalfRe[k][t - 1] = (float)kr;
                    HalfIm[k][t - 1] = (float)ki;
                    HalfA[k][t - 1] = va;
                    HalfB[k][t - 1] = vb;
                }
            }
        }
    }

    public int Radius { get; }

    public int Components { get; }

    // For each component: weights at distance 1..r from the center (the kernels are even), and the center weight.
    public float[][] HalfRe { get; }

    public float[][] HalfIm { get; }

    public float[][] HalfA { get; }

    public float[][] HalfB { get; }

    public float[] CenterRe { get; }

    public float[] CenterIm { get; }

    public float[] CenterA { get; }

    public float[] CenterB { get; }
}

/// <summary>
/// Bokeh blur: the image (premultiplied, encoded values) is raised to <c>gamma</c>, convolved with a disc-like aperture made
/// of complex separable components (<see cref="BokehKernels"/>), and raised to <c>1/gamma</c>, so bright points bloom into
/// discs. A tile reads its rectangle grown by the radius.
/// </summary>
internal sealed class BokehNode : Node
{
    private readonly BokehKernels _kernels;
    private readonly ResampleLayout _layout;
    private readonly EdgeMode _edge;
    private readonly float _gamma;
    private readonly int[] _offA;
    private readonly int[] _offB;

    public BokehNode(Node input, int radius, int components, double gamma, EdgeMode edge)
        : base(
            input.Width,
            input.Height,
            input.Format,
            new OpDescriptor(
                "bokeh_blur",
                new OpParameter("radius", (long)radius),
                new OpParameter("components", (long)components),
                new OpParameter("gamma", gamma),
                new OpParameter("edge", edge.ToString().ToLowerInvariant())),
            input)
    {
        FilterEdge.Validate(edge);
        _layout = new ResampleLayout(input.Format, linear: false);
        _kernels = new BokehKernels(radius, components);
        _edge = edge;
        _gamma = (float)gamma;
        int p = _layout.Stride;
        _offA = new int[radius];
        _offB = new int[radius];
        for (int t = 1; t <= radius; t++)
        {
            _offA[t - 1] = (radius - t) * p;
            _offB[t - 1] = (radius + t) * p;
        }
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        FilterEdge.Need(output, _kernels.Radius, _kernels.Radius, Inputs[0].Width, Inputs[0].Height);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        ImageRect need = RequiredInput(0, o);
        int r = _kernels.Radius;
        int p = _layout.Stride;
        int ibpp = input.Format.BytesPerPixel;
        int padL = need.X - (o.X - r);
        int padR = o.Right + r - need.Right;
        int rowF = (o.Width + (2 * r)) * p;
        int hstride = o.Width * p;
        float[] padded = Pools<float>.Pool.Rent((need.Height + 1) * rowF);
        float[] hRe = Pools<float>.Pool.Rent((need.Height + 1) * hstride);
        float[] hIm = Pools<float>.Pool.Rent((need.Height + 1) * hstride);
        float[] part = Pools<float>.Pool.Rent(hstride);
        byte[] bytes = Pools<byte>.Pool.Rent(need.Width * need.Height * ibpp);
        int[] offA = Pools<int>.Pool.Rent(r);
        int[] offB = Pools<int>.Pool.Rent(r);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, need, bytes, need.Width * ibpp, input.Format, context);
            bool powers = _gamma != 1f;
            for (int y = 0; y < need.Height; y++)
            {
                Span<float> row = padded.AsSpan(y * rowF, rowF);
                ResampleKernel.UnpackRow(bytes.AsSpan(y * need.Width * ibpp, need.Width * ibpp), row[(padL * p)..], need.Width, _layout);
                if (powers)
                {
                    ApplyGamma(row.Slice(padL * p, need.Width * p), _gamma);
                }

                FilterEdge.FillRowPadding(row, p, o.X - r, padL, padR, need.X, need.Width, input.Width, _edge);
            }

            padded.AsSpan(need.Height * rowF, rowF).Clear();
            hRe.AsSpan(need.Height * hstride, hstride).Clear();
            hIm.AsSpan(need.Height * hstride, hstride).Clear();

            int obpp = Format.BytesPerPixel;
            // The horizontal passes of one component cover every row read; the vertical pass sums them per output row, and
            // components add up in a tile-sized buffer.
            float[] sums = Pools<float>.Pool.Rent(o.Height * hstride);
            try
            {
                for (int k = 0; k < _kernels.Components; k++)
                {
                    for (int y = 0; y < need.Height; y++)
                    {
                        ReadOnlySpan<float> src = padded.AsSpan(y * rowF, rowF);
                        FilterKernel.SumPairs(src, r * p, _kernels.CenterRe[k], _offA, _offB, _kernels.HalfRe[k], hRe.AsSpan(y * hstride, hstride));
                        FilterKernel.SumPairs(src, r * p, _kernels.CenterIm[k], _offA, _offB, _kernels.HalfIm[k], hIm.AsSpan(y * hstride, hstride));
                    }

                    for (int y = 0; y < o.Height; y++)
                    {
                        int center = FilterEdge.LocalRow(o.Y + y, input.Height, need.Y, need.Height, _edge);
                        for (int t = 1; t <= r; t++)
                        {
                            int a = FilterEdge.LocalRow(o.Y + y - t, input.Height, need.Y, need.Height, _edge);
                            int b = FilterEdge.LocalRow(o.Y + y + t, input.Height, need.Y, need.Height, _edge);
                            offA[t - 1] = (a < 0 ? need.Height : a) * hstride;
                            offB[t - 1] = (b < 0 ? need.Height : b) * hstride;
                        }

                        Span<float> dst = sums.AsSpan(y * hstride, hstride);
                        if (k == 0)
                        {
                            FilterKernel.SumPairs(hRe, center * hstride, _kernels.CenterA[k], offA.AsSpan(0, r), offB.AsSpan(0, r), _kernels.HalfA[k], dst);
                        }
                        else
                        {
                            FilterKernel.SumPairs(hRe, center * hstride, _kernels.CenterA[k], offA.AsSpan(0, r), offB.AsSpan(0, r), _kernels.HalfA[k], part.AsSpan(0, hstride));
                            Add(dst, part.AsSpan(0, hstride));
                        }

                        FilterKernel.SumPairs(hIm, center * hstride, _kernels.CenterB[k], offA.AsSpan(0, r), offB.AsSpan(0, r), _kernels.HalfB[k], part.AsSpan(0, hstride));
                        Add(dst, part.AsSpan(0, hstride));
                    }
                }

                for (int y = 0; y < o.Height; y++)
                {
                    Span<float> row = sums.AsSpan(y * hstride, hstride);
                    for (int i = 0; i < row.Length; i++)
                    {
                        if (!(row[i] > 0f))
                        {
                            row[i] = 0f;
                        }
                    }

                    if (powers)
                    {
                        ApplyGamma(row, 1f / _gamma);
                    }

                    ResampleKernel.PackRow(row, tile.Span.Slice(y * Tile.Size * obpp, o.Width * obpp), o.Width, _layout);
                }
            }
            finally
            {
                Pools<float>.Pool.Return(sums);
            }

            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
        finally
        {
            Pools<float>.Pool.Return(padded);
            Pools<float>.Pool.Return(hRe);
            Pools<float>.Pool.Return(hIm);
            Pools<float>.Pool.Return(part);
            Pools<byte>.Pool.Return(bytes);
            Pools<int>.Pool.Return(offA);
            Pools<int>.Pool.Return(offB);
        }
    }

    private static void ApplyGamma(Span<float> values, float exponent)
    {
        // Values are non-negative; x^3 and its cube root are exact enough and much cheaper than Pow for the default gamma.
        if (exponent == 3f)
        {
            for (int i = 0; i < values.Length; i++)
            {
                float v = values[i];
                values[i] = v * v * v;
            }
        }
        else if (exponent == 1f / 3f)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = MathF.Cbrt(values[i]);
            }
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = MathF.Pow(values[i], exponent);
            }
        }
    }

    private static void Add(Span<float> dst, ReadOnlySpan<float> src)
    {
        int i = 0;
        int vc = Vector<float>.Count;
        for (; i + vc <= dst.Length; i += vc)
        {
            (new Vector<float>(dst.Slice(i, vc)) + new Vector<float>(src.Slice(i, vc))).CopyTo(dst.Slice(i, vc));
        }

        for (; i < dst.Length; i++)
        {
            dst[i] += src[i];
        }
    }
}
