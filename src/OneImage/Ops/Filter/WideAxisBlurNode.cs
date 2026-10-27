using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// One axis of a Gaussian or box blur whose kernel is far wider than the image is long (sigma more than 16 times the length,
/// or a box radius many times it). Reducing the image cannot help there, so the axis is computed from the kernel directly.
/// <para>
/// Beyond the image the kernel only ever sees edge pixels (clamp), nothing (constant) or the image repeated (mirror), and
/// across the image its weights hardly change: over a length L the Gaussian weight drops by at most
/// <c>exp(-L² / 2σ²)</c>, which is under 0.2% when σ ≥ 16 L. So every output pixel is
/// <c>A·first + B·last + C·(sum of the pixels between)</c>, with A and B the kernel mass that falls on the first and last pixel
/// (clamp only) and C the mass per interior pixel. The masses come from the kernel's cumulative distribution, in closed form.
/// The interior weights are treated as equal; the error that causes stays below 0.1 level at 8 bits (measured: under 0.01), and it is zero for an axis
/// of one or two pixels, where there is no interior. Constant and clamp edges are exact for a box kernel. The per-line
/// aggregates are computed in one forward pass over the input on first use, so a streaming source is read once, in order.
/// </para>
/// </summary>
internal sealed class WideAxisBlurNode : Node
{
    private readonly bool _horizontal;
    private readonly ResampleLayout _layout;
    private readonly double[] _a;
    private readonly double[] _b;
    private readonly double[] _c;
    private readonly int _sumFrom;
    private readonly int _sumTo;
    private readonly object _gate = new();
    private Aggregates? _aggregates;

    private WideAxisBlurNode(Node input, bool horizontal, bool linear, double[] a, double[] b, double[] c, int sumFrom, int sumTo, OpDescriptor descriptor)
        : base(input.Width, input.Height, input.Format, descriptor, input)
    {
        _horizontal = horizontal;
        _layout = new ResampleLayout(input.Format, linear);
        _a = a;
        _b = b;
        _c = c;
        _sumFrom = sumFrom;
        _sumTo = sumTo;
    }

    /// <summary>Blurs <paramref name="input"/> along one axis with a Gaussian of standard deviation <paramref name="sigma"/>.</summary>
    public static WideAxisBlurNode Gaussian(Node input, bool horizontal, double sigma, bool linear, EdgeMode edge)
    {
        double r = Math.Max(1, Math.Ceiling(3 * sigma));
        double rs = (r + 0.5) / sigma;
        double total = Erf(rs / Math.Sqrt(2));
        return Create(
            input,
            horizontal,
            linear,
            edge,
            k => Math.Clamp((Erf((k + 0.5) / sigma / Math.Sqrt(2)) + total) / (2 * total), 0, 1),
            new OpDescriptor(
                "gaussian_blur_wide_axis",
                new OpParameter("axis", horizontal ? "x" : "y"),
                new OpParameter("sigma", sigma),
                new OpParameter("linear", linear),
                new OpParameter("edge", edge.ToString().ToLowerInvariant())));
    }

    /// <summary>Blurs <paramref name="input"/> along one axis with a box of half-width <paramref name="radius"/>.</summary>
    public static WideAxisBlurNode Box(Node input, bool horizontal, int radius, bool linear, EdgeMode edge)
    {
        double width = (2.0 * radius) + 1;
        return Create(
            input,
            horizontal,
            linear,
            edge,
            k => Math.Clamp(k + radius + 1, 0, width) / width,
            new OpDescriptor(
                "box_blur_wide_axis",
                new OpParameter("axis", horizontal ? "x" : "y"),
                new OpParameter("radius", (long)radius),
                new OpParameter("linear", linear),
                new OpParameter("edge", edge.ToString().ToLowerInvariant())));
    }

    private static WideAxisBlurNode Create(Node input, bool horizontal, bool linear, EdgeMode edge, Func<double, double> mass, OpDescriptor descriptor)
    {
        int n = horizontal ? input.Width : input.Height;
        var a = new double[n];
        var b = new double[n];
        var c = new double[n];
        int from = 0;
        int to = n - 1;
        if (n == 1)
        {
            // Every edge mode reads the single pixel for the kernel mass that falls on it; constant keeps only the center tap.
            c[0] = edge == EdgeMode.Constant ? mass(0) - mass(-1) : 1;
        }
        else if (edge == EdgeMode.Mirror)
        {
            // The image repeated, mirrored, covers the wide kernel evenly: each pixel gets the same share.
            Array.Fill(c, 1.0 / n);
        }
        else if (edge == EdgeMode.Constant)
        {
            // Pixels beyond the edge read zero; the kernel mass over the image's own pixels is shared by all of them.
            for (int i = 0; i < n; i++)
            {
                c[i] = (mass(n - 1 - i) - mass(-i - 1)) / n;
            }
        }
        else
        {
            // Clamp: the kernel mass beyond either end lands on the first and last pixel; the rest is shared by the pixels between.
            from = 1;
            to = n - 2;
            for (int i = 0; i < n; i++)
            {
                double left = mass(-i);
                double notRight = mass(n - 2 - i);
                a[i] = left;
                b[i] = 1 - notRight;
                if (n >= 3)
                {
                    c[i] = Math.Max(0, notRight - left) / (n - 2);
                }
            }
        }

        return new WideAxisBlurNode(input, horizontal, linear, a, b, c, from, to, descriptor);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Aggregates ag = GetAggregates(context);
        ImageRect o = TileRect(tx, ty);
        int p = _layout.Stride;
        int bpp = Format.BytesPerPixel;
        float[] row = Pools<float>.Pool.Rent(o.Width * p);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            for (int y = 0; y < o.Height; y++)
            {
                for (int x = 0; x < o.Width; x++)
                {
                    int axis = _horizontal ? o.X + x : o.Y + y;
                    int line = (_horizontal ? o.Y + y : o.X + x) * p;
                    double a = _a[axis];
                    double b = _b[axis];
                    double c = _c[axis];
                    for (int k = 0; k < p; k++)
                    {
                        row[(x * p) + k] = (float)((a * ag.First[line + k]) + (b * ag.Last[line + k]) + (c * ag.Sum[line + k]));
                    }
                }

                ResampleKernel.PackRow(row, tile.Span.Slice(y * Tile.Size * bpp, o.Width * bpp), o.Width, _layout);
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
            Pools<float>.Pool.Return(row);
        }
    }

    private Aggregates GetAggregates(RenderContext context)
    {
        lock (_gate)
        {
            return _aggregates ??= Gather(context);
        }
    }

    // One forward pass in tile order: per line along the blurred axis, the first pixel, the last pixel and the sum of the pixels
    // between (in double, so a very long line loses nothing).
    private Aggregates Gather(RenderContext context)
    {
        Node input = Inputs[0];
        int p = _layout.Stride;
        int ibpp = input.Format.BytesPerPixel;
        int lines = _horizontal ? Height : Width;
        int n = _horizontal ? Width : Height;
        var ag = new Aggregates(new float[lines * p], new float[lines * p], new double[lines * p]);
        byte[] bytes = Pools<byte>.Pool.Rent(Tile.Size * Tile.Size * ibpp);
        float[] px = Pools<float>.Pool.Rent(Tile.Size * p);
        try
        {
            for (int ty = 0; ty < input.TilesY; ty++)
            {
                for (int tx = 0; tx < input.TilesX; tx++)
                {
                    ImageRect r = input.TileRect(tx, ty);
                    Render.ReadRegion(input, r, bytes, r.Width * ibpp, input.Format, context);
                    for (int y = 0; y < r.Height; y++)
                    {
                        ResampleKernel.UnpackRow(bytes.AsSpan(y * r.Width * ibpp, r.Width * ibpp), px, r.Width, _layout);
                        for (int x = 0; x < r.Width; x++)
                        {
                            int axis = _horizontal ? r.X + x : r.Y + y;
                            int line = (_horizontal ? r.Y + y : r.X + x) * p;
                            bool first = axis == 0;
                            bool last = axis == n - 1;
                            bool inside = axis >= _sumFrom && axis <= _sumTo;
                            if (!first && !last && !inside)
                            {
                                continue;
                            }

                            for (int k = 0; k < p; k++)
                            {
                                float v = px[(x * p) + k];
                                if (first)
                                {
                                    ag.First[line + k] = v;
                                }

                                if (last)
                                {
                                    ag.Last[line + k] = v;
                                }

                                if (inside)
                                {
                                    ag.Sum[line + k] += v;
                                }
                            }
                        }
                    }
                }
            }

            return ag;
        }
        finally
        {
            Pools<byte>.Pool.Return(bytes);
            Pools<float>.Pool.Return(px);
        }
    }

    // The error function by its Maclaurin series. The arguments here stay below about 2.3, where it converges in a few dozen
    // terms and the alternating sum loses only two or three digits; from 4 on the function is within 2e-8 of 1, which is plenty.
    internal static double Erf(double x)
    {
        double ax = Math.Abs(x);
        if (ax >= 4)
        {
            return x < 0 ? -1 : 1;
        }

        double x2 = ax * ax;
        double term = ax;
        double sum = ax;
        for (int n = 1; n < 200; n++)
        {
            term *= -x2 / n;
            double t = term / ((2 * n) + 1);
            sum += t;
            if (Math.Abs(t) < 1e-18 * Math.Abs(sum))
            {
                break;
            }
        }

        double v = 2 / Math.Sqrt(Math.PI) * sum;
        return x < 0 ? -v : v;
    }

    private sealed record Aggregates(float[] First, float[] Last, double[] Sum);
}
