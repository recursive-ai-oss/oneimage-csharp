using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>The weights of a neighbourhood filter: a separable pair of 1-D kernels or one 2-D kernel, plus the optional unsharp step.</summary>
internal sealed class ConvolveSpec
{
    /// <summary>Gets the horizontal radius (half the kernel width, rounded down).</summary>
    public int RadiusX { get; init; }

    /// <summary>Gets the vertical radius.</summary>
    public int RadiusY { get; init; }

    /// <summary>Gets the horizontal 1-D kernel (length 2 * RadiusX + 1) of a separable filter, else null.</summary>
    public float[]? Horizontal { get; init; }

    /// <summary>Gets the vertical 1-D kernel of a separable filter, else null.</summary>
    public float[]? Vertical { get; init; }

    /// <summary>Gets the row-major 2-D kernel (height rows of width weights) of a non-separable filter, else null.</summary>
    public float[]? Full { get; init; }

    /// <summary>Gets the value added to every color channel after filtering (working-space units, 1.0 = full scale).</summary>
    public float Offset { get; init; }

    /// <summary>Gets a value indicating whether the result is the unsharp combination <c>orig + amount * (orig - filtered)</c>.</summary>
    public bool Unsharp { get; init; }

    /// <summary>Gets the unsharp amount.</summary>
    public float Amount { get; init; }

    /// <summary>Gets the unsharp threshold: differences smaller than this (working-space units) are left alone.</summary>
    public float Threshold { get; init; }
}

/// <summary>
/// Shared helpers for filters that read a margin around each tile: where the margin comes from at the image edge, and the
/// per-tile working-set arithmetic.
/// </summary>
internal static class FilterEdge
{
    /// <summary>Checks the edge mode a filter can honour; wrap would need pixels from the far side of the image, so it is refused.</summary>
    public static void Validate(EdgeMode mode)
    {
        if (mode is not (EdgeMode.Constant or EdgeMode.Clamp or EdgeMode.Mirror))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Filters support the Clamp, Mirror and Constant edge modes.");
        }
    }

    /// <summary>The input rectangle a filter with the given margins reads for <paramref name="output"/>: the margin box clipped to the image.</summary>
    public static ImageRect Need(ImageRect output, int marginX, int marginY, int width, int height)
    {
        int x0 = Math.Max(0, output.X - marginX);
        int y0 = Math.Max(0, output.Y - marginY);
        int x1 = Math.Min(width, output.Right + marginX);
        int y1 = Math.Min(height, output.Bottom + marginY);
        return new ImageRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Maps an index beyond the image to the in-image index that supplies it, or -1 for the constant (zero) edge.</summary>
    public static int Map(int i, int n, EdgeMode mode) => AffineKernel.MapIndex(i, n, mode);

    /// <summary>
    /// Fills the left and right margin pixels of a padded row (pixels of <paramref name="stride"/> floats): the row holds the
    /// wanted columns starting at <c>firstColumn</c>, of which <paramref name="padLeft"/> lie left of the image and
    /// <paramref name="padRight"/> right of it; the image columns present start at <paramref name="presentX"/>.
    /// </summary>
    public static void FillRowPadding(Span<float> row, int stride, int firstColumn, int padLeft, int padRight, int presentX, int presentWidth, int imageWidth, EdgeMode mode)
    {
        for (int i = 0; i < padLeft; i++)
        {
            CopyMapped(row, stride, i, firstColumn + i, padLeft, presentX, presentWidth, imageWidth, mode);
        }

        int rightStart = padLeft + presentWidth;
        for (int i = 0; i < padRight; i++)
        {
            CopyMapped(row, stride, rightStart + i, presentX + presentWidth + i, padLeft, presentX, presentWidth, imageWidth, mode);
        }
    }

    private static void CopyMapped(Span<float> row, int stride, int slot, int column, int padLeft, int presentX, int presentWidth, int imageWidth, EdgeMode mode)
    {
        int m = Map(column, imageWidth, mode);
        Span<float> dst = row.Slice(slot * stride, stride);
        if (m < 0)
        {
            dst.Clear();
            return;
        }

        int local = m - presentX;
        if ((uint)local >= (uint)presentWidth)
        {
            throw new ImageOperationException("A filter margin pixel fell outside the region that was read.");
        }

        row.Slice((padLeft + local) * stride, stride).CopyTo(dst);
    }

    /// <summary>The local row (in the region read) that supplies image row <paramref name="y"/>, or -1 for the constant edge.</summary>
    public static int LocalRow(int y, int height, int regionY, int regionHeight, EdgeMode mode)
    {
        int m = Map(y, height, mode);
        if (m < 0)
        {
            return -1;
        }

        int local = m - regionY;
        if ((uint)local >= (uint)regionHeight)
        {
            throw new ImageOperationException("A filter margin row fell outside the region that was read.");
        }

        return local;
    }
}

/// <summary>
/// Separable or full 2-D convolution of every channel, optionally followed by the unsharp combination. An output tile reads
/// its input rectangle grown by the kernel radii (clipped to the image; <see cref="FilterEdge"/> supplies the rest), converts
/// it to premultiplied float rows (linear light unless disabled), filters horizontally into an intermediate block, then
/// vertically, and packs the result. Tiles are independent, so results do not depend on scheduling.
/// </summary>
internal sealed class ConvolveNode : Node
{
    private readonly ConvolveSpec _spec;
    private readonly ResampleLayout _layout;
    private readonly EdgeMode _edge;
    private readonly bool _hSymmetric;
    private readonly bool _vSymmetric;

    // Horizontal tap offsets in floats into a padded row (identical for every row).
    private readonly int[] _hOffsets;
    private readonly int[] _hOffsetsB;
    private readonly float[] _hHalf;

    // Full kernel: non-zero taps only.
    private readonly int[] _fullDy;
    private readonly int[] _fullDx;
    private readonly float[] _fullW;

    public ConvolveNode(Node input, ConvolveSpec spec, bool linear, EdgeMode edge, OpDescriptor descriptor)
        : base(input.Width, input.Height, input.Format, descriptor, input)
    {
        FilterEdge.Validate(edge);
        _spec = spec;
        _edge = edge;
        _layout = new ResampleLayout(input.Format, linear);
        int p = _layout.Stride;
        if (spec.Horizontal != null)
        {
            _hSymmetric = IsSymmetric(spec.Horizontal);
            _vSymmetric = IsSymmetric(spec.Vertical!);
        }

        int rx = spec.RadiusX;
        if (_hSymmetric)
        {
            _hOffsets = new int[rx];
            _hOffsetsB = new int[rx];
            _hHalf = new float[rx];
            for (int t = 1; t <= rx; t++)
            {
                _hOffsets[t - 1] = (rx - t) * p;
                _hOffsetsB[t - 1] = (rx + t) * p;
                _hHalf[t - 1] = spec.Horizontal![rx + t];
            }
        }
        else
        {
            _hOffsets = new int[(2 * rx) + 1];
            _hOffsetsB = [];
            _hHalf = [];
            for (int t = 0; t < _hOffsets.Length; t++)
            {
                _hOffsets[t] = t * p;
            }
        }

        if (spec.Full != null)
        {
            int kw = (2 * rx) + 1;
            var dy = new List<int>();
            var dx = new List<int>();
            var w = new List<float>();
            for (int i = 0; i < spec.Full.Length; i++)
            {
                if (spec.Full[i] != 0f)
                {
                    dy.Add(i / kw);
                    dx.Add(i % kw);
                    w.Add(spec.Full[i]);
                }
            }

            _fullDy = [.. dy];
            _fullDx = [.. dx];
            _fullW = [.. w];
        }
        else
        {
            _fullDy = [];
            _fullDx = [];
            _fullW = [];
        }
    }

    private static bool IsSymmetric(float[] w)
    {
        for (int i = 0; i < w.Length / 2; i++)
        {
            if (w[i] != w[w.Length - 1 - i])
            {
                return false;
            }
        }

        return true;
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        FilterEdge.Need(output, _spec.RadiusX, _spec.RadiusY, Inputs[0].Width, Inputs[0].Height);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        ImageRect need = RequiredInput(0, o);
        int p = _layout.Stride;
        int ibpp = input.Format.BytesPerPixel;
        int rx = _spec.RadiusX;
        int ry = _spec.RadiusY;
        int padL = need.X - (o.X - rx);
        int padR = o.Right + rx - need.Right;
        int rowF = (o.Width + (2 * rx)) * p;
        int hstride = o.Width * p;
        bool separable = _spec.Horizontal != null;
        float[] padded = ArrayPool<float>.Shared.Rent((need.Height + 1) * rowF);
        float[]? hbuf = separable ? ArrayPool<float>.Shared.Rent((need.Height + 1) * hstride) : null;
        float[] vrow = ArrayPool<float>.Shared.Rent(hstride);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(need.Width * need.Height * ibpp);
        int tapCount = separable ? (2 * ry) + 1 : _fullW.Length;
        int[] offA = ArrayPool<int>.Shared.Rent(Math.Max(1, tapCount));
        int[] offB = ArrayPool<int>.Shared.Rent(Math.Max(1, ry));
        float[] tapW = ArrayPool<float>.Shared.Rent(Math.Max(1, tapCount));
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, need, bytes, need.Width * ibpp, input.Format, context);
            for (int r = 0; r < need.Height; r++)
            {
                Span<float> row = padded.AsSpan(r * rowF, rowF);
                ResampleKernel.UnpackRow(bytes.AsSpan(r * need.Width * ibpp, need.Width * ibpp), row[(padL * p)..], need.Width, _layout);
                FilterEdge.FillRowPadding(row, p, o.X - rx, padL, padR, need.X, need.Width, input.Width, _edge);
            }

            padded.AsSpan(need.Height * rowF, rowF).Clear();
            ReadOnlySpan<float> hw = _spec.Horizontal;
            if (separable)
            {
                float[] h = hbuf!;
                for (int r = 0; r < need.Height; r++)
                {
                    ReadOnlySpan<float> src = padded.AsSpan(r * rowF, rowF);
                    Span<float> dst = h.AsSpan(r * hstride, hstride);
                    if (_hSymmetric)
                    {
                        FilterKernel.SumPairs(src, rx * p, hw[rx], _hOffsets, _hOffsetsB, _hHalf, dst);
                    }
                    else
                    {
                        FilterKernel.SumTaps(src, _hOffsets, hw, dst);
                    }
                }

                h.AsSpan(need.Height * hstride, hstride).Clear();
            }

            int bpp = Format.BytesPerPixel;
            ReadOnlySpan<float> vw = _spec.Vertical;
            for (int y = 0; y < o.Height; y++)
            {
                if (separable)
                {
                    int center = FilterEdge.LocalRow(o.Y + y, input.Height, need.Y, need.Height, _edge);
                    if (_vSymmetric)
                    {
                        for (int t = 1; t <= ry; t++)
                        {
                            int a = FilterEdge.LocalRow(o.Y + y - t, input.Height, need.Y, need.Height, _edge);
                            int b = FilterEdge.LocalRow(o.Y + y + t, input.Height, need.Y, need.Height, _edge);
                            offA[t - 1] = (a < 0 ? need.Height : a) * hstride;
                            offB[t - 1] = (b < 0 ? need.Height : b) * hstride;
                            tapW[t - 1] = vw[ry + t];
                        }

                        FilterKernel.SumPairs(hbuf!, center * hstride, vw[ry], offA.AsSpan(0, ry), offB.AsSpan(0, ry), tapW.AsSpan(0, ry), vrow.AsSpan(0, hstride));
                    }
                    else
                    {
                        for (int t = 0; t <= 2 * ry; t++)
                        {
                            int a = FilterEdge.LocalRow(o.Y + y + t - ry, input.Height, need.Y, need.Height, _edge);
                            offA[t] = (a < 0 ? need.Height : a) * hstride;
                            tapW[t] = vw[t];
                        }

                        FilterKernel.SumTaps(hbuf!, offA.AsSpan(0, tapCount), tapW.AsSpan(0, tapCount), vrow.AsSpan(0, hstride));
                    }
                }
                else
                {
                    for (int t = 0; t < tapCount; t++)
                    {
                        int a = FilterEdge.LocalRow(o.Y + y + _fullDy[t] - ry, input.Height, need.Y, need.Height, _edge);
                        offA[t] = ((a < 0 ? need.Height : a) * rowF) + (_fullDx[t] * p);
                        tapW[t] = _fullW[t];
                    }

                    FilterKernel.SumTaps(padded, offA.AsSpan(0, tapCount), tapW.AsSpan(0, tapCount), vrow.AsSpan(0, hstride));
                }

                Span<float> filtered = vrow.AsSpan(0, hstride);
                if (_spec.Offset != 0f)
                {
                    AddOffset(filtered, o.Width);
                }

                if (_spec.Unsharp)
                {
                    int orow = FilterEdge.LocalRow(o.Y + y, input.Height, need.Y, need.Height, EdgeMode.Clamp);
                    ReadOnlySpan<float> orig = padded.AsSpan((orow * rowF) + (rx * p), hstride);
                    UnsharpKernel.Combine(orig, filtered, o.Width, _layout, _spec.Amount, _spec.Threshold);
                }

                ResampleKernel.PackRow(filtered, tile.Span.Slice(y * Tile.Size * bpp, o.Width * bpp), o.Width, _layout);
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
            ArrayPool<float>.Shared.Return(padded);
            if (hbuf != null)
            {
                ArrayPool<float>.Shared.Return(hbuf);
            }

            ArrayPool<float>.Shared.Return(vrow);
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<int>.Shared.Return(offA);
            ArrayPool<int>.Shared.Return(offB);
            ArrayPool<float>.Shared.Return(tapW);
        }
    }

    // Adds the offset to the color lanes of premultiplied working pixels (not to alpha or padding lanes).
    private void AddOffset(Span<float> row, int width)
    {
        int p = _layout.Stride;
        int cc = _layout.Colors;
        for (int x = 0; x < width; x++)
        {
            for (int k = 0; k < cc; k++)
            {
                row[(x * p) + k] += _spec.Offset;
            }
        }
    }
}

/// <summary>The unsharp combination of original and filtered working rows.</summary>
internal static class UnsharpKernel
{
    /// <summary>
    /// Replaces <paramref name="filtered"/> (the blurred row) by <c>orig + amount * (orig - blurred)</c> per color channel, leaving
    /// channels whose difference is below <paramref name="threshold"/> as the original. Alpha keeps its original value, and
    /// with alpha the premultiplied color is limited to [0, alpha] so the result stays a valid pixel.
    /// </summary>
    public static void Combine(ReadOnlySpan<float> orig, Span<float> filtered, int width, in ResampleLayout layout, float amount, float threshold)
    {
        int p = layout.Stride;
        int cc = layout.Colors;
        if (orig.Length < width * p || filtered.Length < width * p)
        {
            throw new ArgumentException("Row buffers are too small.");
        }

        bool alpha = layout.HasAlpha;
        for (int x = 0; x < width; x++)
        {
            int i = x * p;
            float a = alpha ? orig[i + cc] : 1f;
            for (int k = 0; k < cc; k++)
            {
                float o = orig[i + k];
                float d = o - filtered[i + k];
                float v = MathF.Abs(d) < threshold ? o : o + (amount * d);
                if (alpha)
                {
                    v = Math.Clamp(v, 0f, a);
                }

                filtered[i + k] = v;
            }

            if (alpha)
            {
                filtered[i + cc] = a;
            }
        }
    }
}
