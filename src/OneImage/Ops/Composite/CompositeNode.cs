using System.Buffers;
using System.Globalization;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Lays an overlay over the base with a blend mode and a Porter-Duff composition (premultiplied). An output tile reads the base tile at the same position and
/// only the part of the overlay that lands on it; tiles the overlay does not touch are the base's tiles, shared.
/// The overlay must already be in the base's color model and profile.
/// </summary>
internal sealed class CompositeNode : Node
{
    private readonly int _x;
    private readonly int _y;
    private readonly float _opacity;
    private readonly BlendMode _blend;
    private readonly AlphaComposition _composition;

    /// <summary>Gets a value indicating whether this node is a throwaway used for one tile (by the layer renderer): its tile skips the shared cache.</summary>
    public bool Ephemeral { get; init; }

    public override bool Cacheable => !Ephemeral;

    public CompositeNode(Node baseNode, Node overlay, int x, int y, float opacity, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver)
        : base(
            baseNode.Width,
            baseNode.Height,
            baseNode.Format,
            new OpDescriptor(
                "composite",
                new OpParameter("mode", BlendNames.Name(blend) ?? "normal"),
                new OpParameter("alpha", BlendNames.Name(composition)),
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
        _blend = blend;
        _composition = composition;
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
        inputIndex == 0 ? output : _opacity <= 0f && BlendNames.EmptyOverlayKeepsBackdrop(_composition) ? new ImageRect(0, 0, 0, 0) : Hit(output);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Tile back = Inputs[0].GetTile(tx, ty, context);
        return TileCompositor.Over(back, o.Width, o.Height, Format, Inputs[1], _x - o.X, _y - o.Y, _opacity, _blend, _composition, inPlace: false, context);
    }
}

/// <summary>
/// Lays an overlay over one backdrop tile without building graph nodes: the arithmetic of <see cref="CompositeNode"/> (which calls it) as a static
/// function, so a renderer that composites many layers per tile pays for the pixels and not for nodes, descriptors and scratch copies.
/// The overlay is read straight from its tiles, one rectangle per tile it lands on.
/// </summary>
internal static class TileCompositor
{
    /// <summary>
    /// Composites <paramref name="overlay"/> onto <paramref name="back"/> (a tile of <paramref name="width"/> x <paramref name="height"/> valid pixels) with the overlay's top-left corner at
    /// (<paramref name="ox"/>, <paramref name="oy"/>) in tile coordinates. Consumes <paramref name="back"/> and returns the result, which is <paramref name="back"/> itself
    /// when nothing changes or when <paramref name="inPlace"/> says the caller owns it exclusively.
    /// </summary>
    public static Tile Over(Tile back, int width, int height, PixelFormat baseFormat, Node overlay, int ox, int oy, float opacity, BlendMode blend, AlphaComposition composition, bool inPlace, RenderContext context)
    {
        ImageRect need = Need(width, height, overlay.Width, overlay.Height, ox, oy, opacity, composition);
        if (need.IsEmpty)
        {
            return back;
        }

        PixelFormat of = overlay.Format;
        bool useCache = !(need.X == 0 && need.Y == 0 && need.Width == overlay.Width && need.Height == overlay.Height && !overlay.Streaming);
        Tile result = back;
        float[]? scratch = null;
        try
        {
            if (!inPlace || !InPlaceSafe(baseFormat, of, blend, composition))
            {
                result = Tile.Rent(back.Length, clear: false);
                back.Span.CopyTo(result.Span);
            }

            int otx0 = need.X >> Tile.Shift;
            int otx1 = (need.Right - 1) >> Tile.Shift;
            int oty0 = need.Y >> Tile.Shift;
            int oty1 = (need.Bottom - 1) >> Tile.Shift;
            for (int oty = oty0; oty <= oty1; oty++)
            {
                for (int otx = otx0; otx <= otx1; otx++)
                {
                    Tile ot = overlay.GetTile(otx, oty, context, useCache);
                    try
                    {
                        using SpillEpochs.Scope read = ot.EnterRead();
                        int tileX = otx << Tile.Shift;
                        int tileY = oty << Tile.Shift;
                        int x0 = Math.Max(need.X, tileX);
                        int y0 = Math.Max(need.Y, tileY);
                        int x1 = Math.Min(need.Right, tileX + Tile.Size);
                        int y1 = Math.Min(need.Bottom, tileY + Tile.Size);
                        Piece(result, back.Span, ot.Span, baseFormat, of, x0 - tileX, y0 - tileY, x1 - x0, y1 - y0, x0 + ox, y0 + oy, opacity, blend, composition, ref scratch);
                    }
                    finally
                    {
                        ot.Release();
                    }
                }
            }
        }
        catch
        {
            if (!ReferenceEquals(result, back))
            {
                result.Release();
            }

            back.Release();
            throw;
        }
        finally
        {
            if (scratch != null)
            {
                Pools<float>.Pool.Return(scratch);
            }
        }

        if (!ReferenceEquals(result, back))
        {
            back.Release();
        }

        return result;
    }

    /// <summary>As <see cref="Over(Tile, int, int, PixelFormat, Node, int, int, float, BlendMode, AlphaComposition, bool, RenderContext)"/> for an overlay that is one finished tile of the same size at the tile's origin. Borrows <paramref name="overlay"/>.</summary>
    public static Tile OverTile(Tile back, int width, int height, PixelFormat baseFormat, Tile overlay, PixelFormat overlayFormat, float opacity, BlendMode blend, AlphaComposition composition, bool inPlace)
    {
        if (opacity <= 0f && BlendNames.EmptyOverlayKeepsBackdrop(composition))
        {
            return back;
        }

        Tile result = back;
        float[]? scratch = null;
        try
        {
            if (!inPlace || !InPlaceSafe(baseFormat, overlayFormat, blend, composition))
            {
                result = Tile.Rent(back.Length, clear: false);
                back.Span.CopyTo(result.Span);
            }

            using SpillEpochs.Scope read = overlay.EnterRead();
            Piece(result, back.Span, overlay.Span, baseFormat, overlayFormat, 0, 0, width, height, 0, 0, opacity, blend, composition, ref scratch);
        }
        catch
        {
            if (!ReferenceEquals(result, back))
            {
                result.Release();
            }

            back.Release();
            throw;
        }
        finally
        {
            if (scratch != null)
            {
                Pools<float>.Pool.Return(scratch);
            }
        }

        if (!ReferenceEquals(result, back))
        {
            back.Release();
        }

        return result;
    }

    /// <summary>The part of the overlay (in overlay coordinates) that lands on a tile of the given size; empty when none does.</summary>
    private static ImageRect Need(int width, int height, int overlayWidth, int overlayHeight, int ox, int oy, float opacity, AlphaComposition composition)
    {
        if (opacity <= 0f && BlendNames.EmptyOverlayKeepsBackdrop(composition))
        {
            return new ImageRect(0, 0, 0, 0);
        }

        long x0 = Math.Max(0L, ox);
        long y0 = Math.Max(0L, oy);
        long x1 = Math.Min((long)width, (long)ox + overlayWidth);
        long y1 = Math.Min((long)height, (long)oy + overlayHeight);
        return x0 >= x1 || y0 >= y1 ? new ImageRect(0, 0, 0, 0) : new ImageRect((int)(x0 - ox), (int)(y0 - oy), (int)(x1 - x0), (int)(y1 - y0));
    }

    // The 3-byte kernel stores 16 bytes per 4 pixels (the spare bytes are overwritten by the next step), which would damage a backdrop it reads from the same memory.
    private static bool InPlaceSafe(PixelFormat baseFormat, PixelFormat overlayFormat, BlendMode blend, AlphaComposition composition) =>
        !(baseFormat == PixelFormat.Rgb8 && overlayFormat == PixelFormat.Rgba8 && blend == BlendMode.Normal && composition == AlphaComposition.SrcOver);

    /// <summary>Blends a rectangle of an overlay tile (tile-local origin <paramref name="sx"/>, <paramref name="sy"/>) onto the result tile at (<paramref name="dx"/>, <paramref name="dy"/>), row by row. The result already holds the backdrop; <paramref name="backdrop"/> is the backdrop tile itself (the same memory when blending in place).</summary>
    private static void Piece(Tile result, ReadOnlySpan<byte> backdrop, ReadOnlySpan<byte> source, PixelFormat bf, PixelFormat of, int sx, int sy, int w, int h, int dx, int dy, float opacity, BlendMode blend, AlphaComposition composition, ref float[]? scratch)
    {
        int bbpp = bf.BytesPerPixel;
        int obpp = of.BytesPerPixel;
        int stride = Tile.Size * bbpp;
        int ostride = Tile.Size * obpp;
        Span<byte> dst = result.Span;
        if (bf == PixelFormat.Rgba8 && of == PixelFormat.Rgba8 && blend == BlendMode.Normal && composition == AlphaComposition.SrcOver)
        {
            for (int r = 0; r < h; r++)
            {
                int at = ((dy + r) * stride) + (dx * bbpp);
                CompositeKernel.OverRgba8(backdrop.Slice(at, w * bbpp), source.Slice(((sy + r) * ostride) + (sx * obpp), w * obpp), dst.Slice(at, w * bbpp), w, opacity);
            }

            return;
        }

        if (bf == PixelFormat.Rgb8 && of == PixelFormat.Rgba8 && blend == BlendMode.Normal && composition == AlphaComposition.SrcOver)
        {
            uint fixedOpacity = CompositeKernel.OpacityFixed(opacity);
            for (int r = 0; r < h; r++)
            {
                int at = ((dy + r) * stride) + (dx * bbpp);
                CompositeKernel.OverRgb8(backdrop.Slice(at, w * bbpp), source.Slice(((sy + r) * ostride) + (sx * obpp), w * obpp), dst.Slice(at, w * bbpp), w, fixedOpacity);
            }

            return;
        }

        // Any format pair: both rows go to premultiplied float, blend, and come back in the base's format.
        var work = new PixelFormat(bf.Model, SampleType.F32, AlphaMode.Premultiplied);
        int channels = work.ChannelCount;
        int n = w * channels;
        scratch ??= Pools<float>.Pool.Rent(3 * Tile.Size * channels);
        Span<float> fb = scratch.AsSpan(0, n);
        Span<float> fo = scratch.AsSpan(Tile.Size * channels, n);
        Span<float> fd = scratch.AsSpan(2 * Tile.Size * channels, n);
        for (int r = 0; r < h; r++)
        {
            int at = ((dy + r) * stride) + (dx * bbpp);
            ConvertKernel.ConvertRow(dst.Slice(at, w * bbpp), bf, SpanCast.AsBytes(fb), work, w);
            ConvertKernel.ConvertRow(source.Slice(((sy + r) * ostride) + (sx * obpp), w * obpp), of, SpanCast.AsBytes(fo), work, w);
            BlendKernel.Row(blend, fb, fo, fd, work.ColorChannels, opacity, composition);
            ConvertKernel.ConvertRow(SpanCast.AsBytes(fd), work, dst.Slice(at, w * bbpp), bf, w);
        }
    }
}
