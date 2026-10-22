using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Resamples the input through a projective transform by inverse mapping, like <see cref="AffineNode"/>. The forward matrix maps source
/// continuous coordinates to destination coordinates and has positive <c>w</c> over the whole source rectangle (checked by the caller);
/// the output covers the destination rectangle (<c>originX</c>, <c>originY</c>, width, height). Output pixel centers are mapped back
/// through the adjugate (oriented so its third row is positive where the forward map is valid); a pixel whose third coordinate is not
/// above <see cref="MinS"/> has no source (it is on or beyond the horizon) and gets the background.
/// </summary>
internal sealed class ProjectiveNode : Node
{
    // Smallest third coordinate of the max-normalized inverse that still counts as a point in front of the viewer.
    internal const double MinS = 1e-12;

    // Positions farther than this from the image are clamped; they lie outside any image the engine can hold, so they only select the background.
    private const double FarAway = 1e9;

    private readonly ResampleLayout _layout;
    private readonly EdgeMode _edge;
    private readonly ResamplingFilter _filter;
    private readonly float[] _background;
    private readonly double _originX;
    private readonly double _originY;
    private readonly double _i11;
    private readonly double _i12;
    private readonly double _i13;
    private readonly double _i21;
    private readonly double _i22;
    private readonly double _i23;
    private readonly double _i31;
    private readonly double _i32;
    private readonly double _i33;

    public ProjectiveNode(Node input, ProjectiveMatrix m, double originX, double originY, int width, int height, ResamplingFilter filter, EdgeMode edge, Rgba32 background, bool linear)
        : base(
            width,
            height,
            input.Format,
            new OpDescriptor(
                "perspective",
                new OpParameter("m11", m.M11),
                new OpParameter("m12", m.M12),
                new OpParameter("m13", m.M13),
                new OpParameter("m21", m.M21),
                new OpParameter("m22", m.M22),
                new OpParameter("m23", m.M23),
                new OpParameter("m31", m.M31),
                new OpParameter("m32", m.M32),
                new OpParameter("m33", m.M33),
                new OpParameter("origin_x", originX),
                new OpParameter("origin_y", originY),
                new OpParameter("width", (long)width),
                new OpParameter("height", (long)height),
                new OpParameter("filter", FilterFunctions.Name(filter)),
                new OpParameter("edge", edge.ToString().ToLowerInvariant()),
                new OpParameter("linear", linear),
                new OpParameter("red", (long)background.R),
                new OpParameter("green", (long)background.G),
                new OpParameter("blue", (long)background.B),
                new OpParameter("alpha", (long)background.A)),
            input)
    {
        double det = m.Determinant;
        double max = Math.Max(Math.Max(Math.Max(Math.Abs(m.M11), Math.Abs(m.M12)), Math.Max(Math.Abs(m.M13), Math.Abs(m.M21))), Math.Max(Math.Max(Math.Abs(m.M22), Math.Abs(m.M23)), Math.Max(Math.Abs(m.M31), Math.Max(Math.Abs(m.M32), Math.Abs(m.M33)))));
        if (!double.IsFinite(det) || !(max > 0) || Math.Abs(det) <= 1e-12 * max * max * max)
        {
            throw new ImageOperationException("The perspective transform is singular or not finite.");
        }

        // adjugate * sign(det) is the inverse times |det|: its third row is positive exactly where the forward w is.
        ProjectiveMatrix a = ProjectiveMatrix.Adjugate(m);
        double k = Math.Sign(det) / MaxAbs(a);
        _i11 = a.M11 * k;
        _i12 = a.M12 * k;
        _i13 = a.M13 * k;
        _i21 = a.M21 * k;
        _i22 = a.M22 * k;
        _i23 = a.M23 * k;
        _i31 = a.M31 * k;
        _i32 = a.M32 * k;
        _i33 = a.M33 * k;
        _originX = originX;
        _originY = originY;
        _layout = new ResampleLayout(input.Format, linear);
        _edge = edge;
        _filter = filter;

        byte[] pixel = ExtendNode.BackgroundPixel(input.Format, background);
        float[] tmp = new float[_layout.Stride];
        ResampleKernel.UnpackRow(pixel, tmp, 1, _layout);
        _background = tmp;
    }

    private static double MaxAbs(ProjectiveMatrix a) =>
        Math.Max(Math.Max(Math.Max(Math.Abs(a.M11), Math.Abs(a.M12)), Math.Max(Math.Abs(a.M13), Math.Abs(a.M21))), Math.Max(Math.Max(Math.Abs(a.M22), Math.Abs(a.M23)), Math.Max(Math.Abs(a.M31), Math.Max(Math.Abs(a.M32), Math.Abs(a.M33)))));

    // Fills interleaved source index-space positions for columns [x0, x0 + count) of output row y; NaN where there is no source.
    // Every expression uses the absolute output coordinates, so any tile split gives the same numbers, and there is no fused multiply-add.
    private void Positions(int y, int x0, int count, Span<double> positions)
    {
        double yc = _originY + y + 0.5;
        double u0 = (_i12 * yc) + _i13;
        double v0 = (_i22 * yc) + _i23;
        double s0 = (_i32 * yc) + _i33;
        for (int i = 0; i < count; i++)
        {
            double xc = _originX + x0 + i + 0.5;
            double s = (_i31 * xc) + s0;
            double px;
            double py;
            if (s > MinS)
            {
                px = ((((_i11 * xc) + u0)) / s) - 0.5;
                py = ((((_i21 * xc) + v0)) / s) - 0.5;
                if (!(Math.Abs(px) < FarAway) || !(Math.Abs(py) < FarAway))
                {
                    px = double.NaN;
                    py = double.NaN;
                }
            }
            else
            {
                px = double.NaN;
                py = double.NaN;
            }

            positions[2 * i] = px;
            positions[(2 * i) + 1] = py;
        }
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        Node input = Inputs[0];
        if (output.Width <= 0 || output.Height <= 0)
        {
            return new ImageRect(0, 0, 0, 0);
        }

        // The pixel centers of the tile form a rectangle in destination space. Cut it to the half-plane in front of the horizon and
        // map the corners of what is left: a projective map takes a convex polygon on that side to a convex polygon, whose
        // extremes are at its corners. The result is exact up to the one-pixel margin that absorbs rounding at the cut.
        double xa = _originX + output.X + 0.5;
        double xb = _originX + output.Right - 0.5;
        double ya = _originY + output.Y + 0.5;
        double yb = _originY + output.Bottom - 0.5;
        Span<(double X, double Y)> poly = stackalloc (double, double)[4] { (xa, ya), (xb, ya), (xb, yb), (xa, yb) };
        Span<(double X, double Y)> clipped = stackalloc (double, double)[8];
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            (double ax, double ay) = poly[i];
            (double bx, double by) = poly[(i + 1) & 3];
            double sa = (_i31 * ax) + (_i32 * ay) + _i33 - MinS;
            double sb = (_i31 * bx) + (_i32 * by) + _i33 - MinS;
            if (sa > 0)
            {
                clipped[n++] = (ax, ay);
            }

            if ((sa > 0) != (sb > 0))
            {
                double t = sa / (sa - sb);
                clipped[n++] = (ax + (t * (bx - ax)), ay + (t * (by - ay)));
            }
        }

        if (n == 0)
        {
            return new ImageRect(0, 0, 0, 0);
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            (double x, double y) = clipped[i];
            double s = Math.Max((_i31 * x) + (_i32 * y) + _i33, MinS);
            double px = Math.Clamp((((_i11 * x) + (_i12 * y) + _i13) / s) - 0.5, -FarAway, FarAway);
            double py = Math.Clamp((((_i21 * x) + (_i22 * y) + _i23) / s) - 0.5, -FarAway, FarAway);
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }

        int support = AffineFilterTable.Support(_filter);
        (int x0, int x1) = AffineNode.Hull(_edge, AffineNode.Tap(minX - 1, maxX + 1, support), input.Width);
        (int y0, int y1) = AffineNode.Hull(_edge, AffineNode.Tap(minY - 1, maxY + 1, support), input.Height);
        return x0 > x1 || y0 > y1 ? new ImageRect(0, 0, 0, 0) : new ImageRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        ImageRect need = RequiredInput(0, o);
        Node input = Inputs[0];
        int p = _layout.Stride;
        int ibpp = input.Format.BytesPerPixel;
        int obpp = Format.BytesPerPixel;
        int bandRows = Math.Min(Tile.Size, Math.Max(need.Height, 1));
        float[] region = ArrayPool<float>.Shared.Rent(Math.Max(1, need.Width * need.Height * p));
        byte[] band = ArrayPool<byte>.Shared.Rent(Math.Max(1, bandRows * need.Width * ibpp));
        float[] row = ArrayPool<float>.Shared.Rent(o.Width * p);
        double[] pos = ArrayPool<double>.Shared.Rent(o.Width * 2);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            // Pull the source rectangle in bands of rows and convert it to premultiplied (linear) floats.
            for (int y = 0, n; y < need.Height; y += n)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                // Bands stop at tile-row boundaries so no source tile is fetched twice.
                n = Math.Min(Tile.Size - ((need.Y + y) & (Tile.Size - 1)), need.Height - y);
                Render.ReadRegion(input, new ImageRect(need.X, need.Y + y, need.Width, n), band, need.Width * ibpp, input.Format, context);
                for (int r = 0; r < n; r++)
                {
                    ResampleKernel.UnpackRow(
                        band.AsSpan(r * need.Width * ibpp, need.Width * ibpp),
                        region.AsSpan((y + r) * need.Width * p, need.Width * p),
                        need.Width,
                        _layout);
                }
            }

            ReadOnlySpan<float> rgn = region.AsSpan(0, need.Width * need.Height * p);
            for (int r = 0; r < o.Height; r++)
            {
                Positions(o.Y + r, o.X, o.Width, pos);
                AffineKernel.SampleRow(
                    rgn,
                    need.Width,
                    need.Height,
                    need.X,
                    need.Y,
                    input.Width,
                    input.Height,
                    p,
                    _edge,
                    _background,
                    _filter,
                    0,
                    0,
                    0,
                    0,
                    o.X,
                    o.Width,
                    row,
                    pos.AsSpan(0, o.Width * 2));
                ResampleKernel.PackRow(row, tile.Span.Slice(r * Tile.Size * obpp, o.Width * obpp), o.Width, _layout);
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
            ArrayPool<float>.Shared.Return(region);
            ArrayPool<byte>.Shared.Return(band);
            ArrayPool<float>.Shared.Return(row);
            ArrayPool<double>.Shared.Return(pos);
        }
    }
}
