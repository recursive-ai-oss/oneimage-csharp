using System.Buffers;
using System.Numerics;
using OneImage.Kernels;

namespace OneImage;

/// <summary>How the responses of an edge detector's kernels become one value.</summary>
internal enum EdgeCombine
{
    /// <summary>One kernel; negative responses become 0.</summary>
    Single,

    /// <summary>Two kernels (horizontal and vertical); sqrt(gx² + gy²).</summary>
    Magnitude,

    /// <summary>Several rotated kernels; the largest response (never below 0).</summary>
    Max,
}

/// <summary>A small convolution kernel. Tap (i, j) reads the pixel at (x + i − AnchorX, y + j − AnchorY); the anchor is size / 2.</summary>
internal sealed class EdgeKernel(int width, int height, float[] weights)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public float[] Weights { get; } = weights;

    public int AnchorX => Width >> 1;

    public int AnchorY => Height >> 1;
}

/// <summary>The kernels of the known edge detectors.</summary>
internal static class EdgeKernels
{
    public static (EdgeKernel[] Kernels, EdgeCombine Combine) For(EdgeDetector detector) => detector switch
    {
        EdgeDetector.Sobel => (Pair(3, [-1, 0, 1, -2, 0, 2, -1, 0, 1], [-1, -2, -1, 0, 0, 0, 1, 2, 1]), EdgeCombine.Magnitude),
        EdgeDetector.Prewitt => (Pair(3, [-1, 0, 1, -1, 0, 1, -1, 0, 1], [1, 1, 1, 0, 0, 0, -1, -1, -1]), EdgeCombine.Magnitude),
        EdgeDetector.Scharr => (Pair(3, [-3, 0, 3, -10, 0, 10, -3, 0, 3], [3, 10, 3, 0, 0, 0, -3, -10, -3]), EdgeCombine.Magnitude),
        EdgeDetector.RobertsCross => (Pair(2, [1, 0, 0, -1], [0, 1, -1, 0]), EdgeCombine.Magnitude),
        EdgeDetector.Kayyali => (Pair(3, [6, 0, -6, 0, 0, 0, -6, 0, 6], [-6, 0, 6, 0, 0, 0, 6, 0, -6]), EdgeCombine.Magnitude),
        EdgeDetector.Laplacian3x3 => ([new EdgeKernel(3, 3, [-1, -1, -1, -1, 8, -1, -1, -1, -1])], EdgeCombine.Single),
        EdgeDetector.Laplacian5x5 => ([new EdgeKernel(5, 5, [-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 24, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1])], EdgeCombine.Single),
        EdgeDetector.LaplacianOfGaussian => ([new EdgeKernel(5, 5, [0, 0, -1, 0, 0, 0, -1, -2, -1, 0, -1, -2, 16, -2, -1, 0, -1, -2, -1, 0, 0, 0, -1, 0, 0])], EdgeCombine.Single),
        EdgeDetector.Kirsch => (Compass([5, 5, 5, -3, -3, -3, -3, -3]), EdgeCombine.Max),
        EdgeDetector.Robinson => (Compass([1, 2, 1, 0, -1, -2, -1, 0]), EdgeCombine.Max),
        _ => throw new ArgumentOutOfRangeException(nameof(detector)),
    };

    private static EdgeKernel[] Pair(int size, float[] x, float[] y) => [new EdgeKernel(size, size, x), new EdgeKernel(size, size, y)];

    // The eight rotations of a ring of border weights (top-left, top, top-right, right, bottom-right, bottom, bottom-left, left),
    // with a zero center.
    private static EdgeKernel[] Compass(float[] ring)
    {
        int[] cell = [0, 1, 2, 5, 8, 7, 6, 3];
        var kernels = new EdgeKernel[8];
        for (int rotation = 0; rotation < 8; rotation++)
        {
            float[] w = new float[9];
            for (int i = 0; i < 8; i++)
            {
                w[cell[(i + rotation) % 8]] = ring[i];
            }

            kernels[rotation] = new EdgeKernel(3, 3, w);
        }

        return kernels;
    }
}

/// <summary>
/// Edge detection by small kernels. The input is read as straight float samples (gray, or the color channels; with
/// <c>grayscale</c> the Rec. 709 luma of the stored values, taken from an 8-bit gray image first when the input is 8-bit so
/// the kernels see exactly the values a separate grayscale step would give). Each kernel is a weighted sum over its window of
/// the stored values; the responses are combined as <see cref="EdgeCombine"/> says, and results are clamped below at 0.
/// Alpha, when kept, is the pixel's own. An output tile reads its rectangle plus a margin of the largest kernel extent.
/// </summary>
internal sealed class EdgeDetectNode : Node
{
    private readonly EdgeKernel[] _kernels;
    private readonly EdgeCombine _combine;
    private readonly bool _grayscale;
    private readonly EdgeMode _edge;
    private readonly PixelFormat _work;
    private readonly int _margin;
    private readonly int[][] _dy;
    private readonly int[][] _dx;
    private readonly float[][] _w;

    public EdgeDetectNode(Node input, EdgeDetector detector, bool grayscale, EdgeMode edge)
        : base(
            input.Width,
            input.Height,
            OutputFormat(input.Format, grayscale),
            new OpDescriptor(
                "detect_edges",
                new OpParameter("detector", detector.ToString()),
                new OpParameter("grayscale", grayscale),
                new OpParameter("edge", edge.ToString().ToLowerInvariant())),
            input)
    {
        FilterEdge.Validate(edge);
        if (input.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
        {
            throw new ImageOperationException($"Edge detection is defined for gray and RGB images, not {input.Format.Model}.");
        }

        (_kernels, _combine) = EdgeKernels.For(detector);
        _grayscale = grayscale;
        _edge = edge;
        _work = new PixelFormat(Format.Model, SampleType.F32, Format.Alpha);
        foreach (EdgeKernel k in _kernels)
        {
            _margin = Math.Max(_margin, Math.Max(Math.Max(k.AnchorX, k.Width - 1 - k.AnchorX), Math.Max(k.AnchorY, k.Height - 1 - k.AnchorY)));
        }

        _dy = new int[_kernels.Length][];
        _dx = new int[_kernels.Length][];
        _w = new float[_kernels.Length][];
        for (int n = 0; n < _kernels.Length; n++)
        {
            EdgeKernel k = _kernels[n];
            var dy = new List<int>();
            var dx = new List<int>();
            var w = new List<float>();
            for (int j = 0; j < k.Height; j++)
            {
                for (int i = 0; i < k.Width; i++)
                {
                    float v = k.Weights[(j * k.Width) + i];
                    if (v != 0f)
                    {
                        dy.Add(j - k.AnchorY);
                        dx.Add(i - k.AnchorX);
                        w.Add(v);
                    }
                }
            }

            _dy[n] = [.. dy];
            _dx[n] = [.. dx];
            _w[n] = [.. w];
        }
    }

    private static PixelFormat OutputFormat(PixelFormat f, bool grayscale) =>
        grayscale ? new PixelFormat(ColorModel.Gray, f.Sample) : new PixelFormat(f.Model, f.Sample, f.HasAlpha ? AlphaMode.Straight : AlphaMode.None);

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        FilterEdge.Need(output, _margin, _margin, Inputs[0].Width, Inputs[0].Height);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        ImageRect need = RequiredInput(0, o);
        int m = _margin;
        int lanes = _work.ChannelCount;
        bool keepAlpha = _work.HasAlpha;
        int ibpp = input.Format.BytesPerPixel;
        int obpp = Format.BytesPerPixel;
        int padL = need.X - (o.X - m);
        int padR = o.Right + m - need.Right;
        int rowF = (o.Width + (2 * m)) * lanes;
        int outLen = o.Width * lanes;
        int maxTaps = 1;
        foreach (float[] w in _w)
        {
            maxTaps = Math.Max(maxTaps, w.Length);
        }

        byte[] bytes = Pools<byte>.Pool.Rent(need.Width * need.Height * ibpp);
        float[] padded = Pools<float>.Pool.Rent((need.Height + 1) * rowF);
        float[] resp = Pools<float>.Pool.Rent(_kernels.Length * outLen);
        float[] result = Pools<float>.Pool.Rent(outLen);
        int[] offsets = Pools<int>.Pool.Rent(maxTaps);
        byte[]? gray8 = _grayscale && input.Format.Sample == SampleType.U8 && input.Format.Model == ColorModel.Rgb ? Pools<byte>.Pool.Rent(need.Width) : null;
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, need, bytes, need.Width * ibpp, input.Format, context);
            for (int y = 0; y < need.Height; y++)
            {
                ReadOnlySpan<byte> src = bytes.AsSpan(y * need.Width * ibpp, need.Width * ibpp);
                Span<float> row = padded.AsSpan(y * rowF, rowF);
                Span<byte> dstBytes = SpanCast.AsBytes(row[(padL * lanes)..]);
                if (gray8 != null)
                {
                    ConvertKernel.ConvertRow(src, input.Format, gray8, PixelFormat.Gray8, need.Width);
                    ConvertKernel.ConvertRow(gray8.AsSpan(0, need.Width), PixelFormat.Gray8, dstBytes, _work, need.Width);
                }
                else
                {
                    ConvertKernel.ConvertRow(src, input.Format, dstBytes, _work, need.Width);
                }

                FilterEdge.FillRowPadding(row, lanes, o.X - m, padL, padR, need.X, need.Width, input.Width, _edge);
            }

            padded.AsSpan(need.Height * rowF, rowF).Clear();
            for (int y = 0; y < o.Height; y++)
            {
                for (int n = 0; n < _kernels.Length; n++)
                {
                    float[] w = _w[n];
                    for (int t = 0; t < w.Length; t++)
                    {
                        int lr = FilterEdge.LocalRow(o.Y + y + _dy[n][t], input.Height, need.Y, need.Height, _edge);
                        offsets[t] = ((lr < 0 ? need.Height : lr) * rowF) + ((_dx[n][t] + m) * lanes);
                    }

                    FilterKernel.SumTaps(padded, offsets.AsSpan(0, w.Length), w, resp.AsSpan(n * outLen, outLen));
                }

                Combine(resp, _kernels.Length, outLen, _combine, result);
                if (keepAlpha)
                {
                    int center = FilterEdge.LocalRow(o.Y + y, input.Height, need.Y, need.Height, EdgeMode.Clamp);
                    int colors = lanes - 1;
                    for (int x = 0; x < o.Width; x++)
                    {
                        result[(x * lanes) + colors] = padded[(center * rowF) + ((x + m) * lanes) + colors];
                    }
                }

                ConvertKernel.ConvertRow(SpanCast.AsBytes<float>(result.AsSpan(0, outLen)), _work, tile.Span.Slice(y * Tile.Size * obpp, o.Width * obpp), Format, o.Width);
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
            Pools<byte>.Pool.Return(bytes);
            Pools<float>.Pool.Return(padded);
            Pools<float>.Pool.Return(resp);
            Pools<float>.Pool.Return(result);
            Pools<int>.Pool.Return(offsets);
            if (gray8 != null)
            {
                Pools<byte>.Pool.Return(gray8);
            }
        }
    }

    private static void Combine(float[] resp, int count, int len, EdgeCombine mode, float[] result)
    {
        int vc = Vector<float>.Count;
        int i = 0;
        for (; i + vc <= len; i += vc)
        {
            Vector<float> v = new(resp.AsSpan(i, vc));
            switch (mode)
            {
                case EdgeCombine.Magnitude:
                    Vector<float> b = new(resp.AsSpan(len + i, vc));
                    v = Vector.SquareRoot((v * v) + (b * b));
                    break;
                case EdgeCombine.Max:
                    for (int n = 1; n < count; n++)
                    {
                        v = Vector.Max(v, new Vector<float>(resp.AsSpan((n * len) + i, vc)));
                    }

                    v = Vector.Max(v, Vector<float>.Zero);
                    break;
                default:
                    v = Vector.Max(v, Vector<float>.Zero);
                    break;
            }

            v.CopyTo(result.AsSpan(i, vc));
        }

        for (; i < len; i++)
        {
            float v = resp[i];
            switch (mode)
            {
                case EdgeCombine.Magnitude:
                    float b = resp[len + i];
                    v = MathF.Sqrt((v * v) + (b * b));
                    break;
                case EdgeCombine.Max:
                    for (int n = 1; n < count; n++)
                    {
                        v = MathF.Max(v, resp[(n * len) + i]);
                    }

                    v = MathF.Max(v, 0f);
                    break;
                default:
                    v = MathF.Max(v, 0f);
                    break;
            }

            result[i] = v;
        }
    }
}
