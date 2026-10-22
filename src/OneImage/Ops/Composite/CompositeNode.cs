using System.Buffers;
using System.Globalization;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Lays an overlay over the base (source-over, premultiplied). An output tile reads the base tile at the same position and
/// only the part of the overlay that lands on it; tiles the overlay does not touch are the base's tiles, shared.
/// The overlay must already be in the base's color model and profile.
/// </summary>
internal sealed class CompositeNode : Node
{
    private readonly int _x;
    private readonly int _y;
    private readonly float _opacity;
    private readonly PixelFormat _work;

    public CompositeNode(Node baseNode, Node overlay, int x, int y, float opacity)
        : base(
            baseNode.Width,
            baseNode.Height,
            baseNode.Format,
            new OpDescriptor(
                "composite",
                new OpParameter("mode", "normal"),
                new OpParameter("x", (long)x),
                new OpParameter("y", (long)y),
                new OpParameter("opacity", (double)opacity)),
            baseNode,
            overlay)
    {
        if (baseNode.Format.Model != overlay.Format.Model)
        {
            throw new ImageOperationException("Composite needs the overlay in the base's color model.");
        }

        _x = x;
        _y = y;
        _opacity = opacity;
        _work = new PixelFormat(baseNode.Format.Model, SampleType.F32, AlphaMode.Premultiplied);
    }

    /// <summary>The part of the overlay (in overlay coordinates) that lands on <paramref name="output"/>; empty when none does.</summary>
    private ImageRect Hit(ImageRect output)
    {
        Node ov = Inputs[1];
        long x0 = Math.Max((long)output.X, _x);
        long y0 = Math.Max((long)output.Y, _y);
        long x1 = Math.Min((long)output.Right, (long)_x + ov.Width);
        long y1 = Math.Min((long)output.Bottom, (long)_y + ov.Height);
        return x0 >= x1 || y0 >= y1 ? new ImageRect(0, 0, 0, 0) : new ImageRect((int)(x0 - _x), (int)(y0 - _y), (int)(x1 - x0), (int)(y1 - y0));
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        inputIndex == 0 ? output : _opacity <= 0f ? new ImageRect(0, 0, 0, 0) : Hit(output);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        ImageRect need = RequiredInput(1, o);
        Tile back = Inputs[0].GetTile(tx, ty, context);
        if (need.IsEmpty)
        {
            return back;
        }

        PixelFormat bf = Format;
        PixelFormat of = Inputs[1].Format;
        int bbpp = bf.BytesPerPixel;
        int obpp = of.BytesPerPixel;
        int ostride = need.Width * obpp;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(ostride * need.Height);
        Tile? result = null;
        try
        {
            Render.ReadRegion(Inputs[1], need, scratch, ostride, of, context);
            result = Tile.Rent(back.Length, clear: false);
            back.Span.CopyTo(result.Span);
            int tileStride = Tile.Size * bbpp;
            int col = (need.X + _x) - o.X; // first affected column inside the tile
            int row0 = (need.Y + _y) - o.Y;
            bool fast = bf == PixelFormat.Rgb8 && of == PixelFormat.Rgba8;
            uint fixedOpacity = CompositeKernel.OpacityFixed(_opacity);
            if (fast)
            {
                for (int r = 0; r < need.Height; r++)
                {
                    int at = ((row0 + r) * tileStride) + (col * bbpp);
                    CompositeKernel.OverRgb8(back.Span.Slice(at, need.Width * bbpp), scratch.AsSpan(r * ostride, ostride), result.Span.Slice(at, need.Width * bbpp), need.Width, fixedOpacity);
                }
            }
            else
            {
                BlendGeneral(back, result, scratch, need, of, col, row0);
            }

            return result;
        }
        catch
        {
            result?.Release();
            throw;
        }
        finally
        {
            back.Release();
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    // Any format pair: both rows go to premultiplied float, blend, and come back in the base's format.
    private void BlendGeneral(Tile back, Tile result, byte[] overlayBytes, ImageRect need, PixelFormat of, int col, int row0)
    {
        PixelFormat bf = Format;
        int bbpp = bf.BytesPerPixel;
        int obpp = of.BytesPerPixel;
        int ostride = need.Width * obpp;
        int tileStride = Tile.Size * bbpp;
        int channels = _work.ChannelCount;
        int n = need.Width * channels;
        float[] fb = ArrayPool<float>.Shared.Rent(n);
        float[] fo = ArrayPool<float>.Shared.Rent(n);
        float[] fd = ArrayPool<float>.Shared.Rent(n);
        try
        {
            for (int r = 0; r < need.Height; r++)
            {
                int at = ((row0 + r) * tileStride) + (col * bbpp);
                ConvertKernel.ConvertRow(back.Span.Slice(at, need.Width * bbpp), bf, AsBytes(fb, n), _work, need.Width);
                ConvertKernel.ConvertRow(overlayBytes.AsSpan(r * ostride, ostride), of, AsBytes(fo, n), _work, need.Width);
                CompositeKernel.BlendRow<Normal>(fb.AsSpan(0, n), fo.AsSpan(0, n), fd.AsSpan(0, n), _work.ColorChannels, _opacity);
                ConvertKernel.ConvertRow(AsBytes(fd, n), _work, result.Span.Slice(at, need.Width * bbpp), bf, need.Width);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(fb);
            ArrayPool<float>.Shared.Return(fo);
            ArrayPool<float>.Shared.Return(fd);
        }
    }

    private static Span<byte> AsBytes(float[] f, int count) => SpanCast.AsBytes(f.AsSpan(0, count));
}
