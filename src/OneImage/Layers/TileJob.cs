using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Layers;

/// <summary>A node that holds one finished tile, so a throwaway graph for a single tile can read it. It owns no reference of its own.</summary>
internal sealed class HeldTileNode : Node
{
    private readonly Tile _tile;

    public HeldTileNode(Tile tile, ImageRect rect, PixelFormat format)
        : base(rect.Width, rect.Height, format, new OpDescriptor("tile"))
    {
        _tile = tile;
    }

    public override bool Cacheable => false;

    public override bool DirectStorage => true;

    protected override bool PipelinePrivate => false;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context) => _tile.Retain();
}

/// <summary>
/// Computes one document tile: folds the layer tree over the background for that tile only. Every method takes ownership of the tile it is
/// given (releasing it, also when it fails) and returns a tile the caller owns; tiles are never modified once produced, so a result may be
/// the very tile that came in.
/// </summary>
internal sealed class TileJob
{
    private readonly DocumentSnapshot _doc;
    private readonly ImageRect _rect;
    private readonly RenderContext _context;
    private readonly PixelFormat _format;
    private readonly PixelFormat _work;
    private readonly Tile _zero;

    public TileJob(DocumentSnapshot doc, ImageRect rect, RenderContext context)
    {
        _doc = doc;
        _rect = rect;
        _context = context;
        _format = doc.Format;
        _work = new PixelFormat(_format.Model, SampleType.F32, AlphaMode.Premultiplied);
        _zero = Tile.Zero(_format);
    }

    public Tile Run() => Fold(_doc.Layers, _doc.Background.Retain());

    private Tile Fold(LayerState[] layers, Tile back)
    {
        int i = 0;
        while (i < layers.Length)
        {
            try
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                back.Release();
                throw;
            }

            LayerState l = layers[i];

            // The layers after this one that are clipped to it. An adjustment cannot be a clipping base: what follows is drawn as ordinary layers.
            int j = i + 1;
            if (l.Kind != LayerKind.Adjustment)
            {
                while (j < layers.Length && layers[j].Clip)
                {
                    j++;
                }
            }

            if (l.Visible && l.Opacity > 0f)
            {
                back = j == i + 1 ? Apply(l, back) : ApplyClipRun(layers, i, j, back);
            }

            i = j;
        }

        return back;
    }

    private Tile Apply(LayerState l, Tile back) => l.Kind switch
    {
        LayerKind.Pixel or LayerKind.Fill => ApplyContent(l, back),
        LayerKind.Adjustment => ApplyAdjustment(l, back),
        _ => ApplyGroup(l, back),
    };

    private Tile ApplyContent(LayerState l, Tile back)
    {
        Tile? content = null;
        try
        {
            if (!Touches(l))
            {
                return back;
            }

            if (l.Mask != null)
            {
                content = MaskedContent(l);
                if (content == null)
                {
                    return back;
                }
            }
        }
        catch
        {
            back.Release();
            throw;
        }

        return content == null
            ? Over(back, l.Node!, l.Bounds.X, l.Bounds.Y, l.Opacity, l.Blend, AlphaComposition.SrcOver)
            : OverHeld(back, content, l.Opacity, l.Blend, AlphaComposition.SrcOver);
    }

    private Tile ApplyAdjustment(LayerState l, Tile back)
    {
        byte[]? coverage = null;
        try
        {
            Tile adjusted;
            try
            {
                if (l.Mask != null)
                {
                    coverage = Coverage(l.Mask, out bool zero, out _);
                    if (zero)
                    {
                        return back;
                    }
                }

                adjusted = Adjusted(l.Adjustment!, back);
                if (l.Blend != BlendMode.Normal)
                {
                    Tile blended;
                    try
                    {
                        blended = Over(back.Retain(), new HeldTileNode(adjusted, _rect, _format), _rect.X, _rect.Y, 1f, l.Blend, AlphaComposition.SrcOver);
                    }
                    finally
                    {
                        adjusted.Release();
                    }

                    adjusted = blended;
                }
            }
            catch
            {
                back.Release();
                throw;
            }

            return Mix(back, adjusted, coverage, l.Opacity);
        }
        finally
        {
            if (coverage != null)
            {
                ArrayPool<byte>.Shared.Return(coverage);
            }
        }
    }

    private Tile ApplyGroup(LayerState l, Tile back)
    {
        if (l.Children.Length == 0)
        {
            return back;
        }

        byte[]? coverage = null;
        try
        {
            Tile? inner = null;
            Tile? group = null;
            try
            {
                if (l.Mask != null)
                {
                    coverage = Coverage(l.Mask, out bool zero, out bool full);
                    if (zero)
                    {
                        return back;
                    }

                    if (full)
                    {
                        ArrayPool<byte>.Shared.Return(coverage);
                        coverage = null;
                    }
                }

                if (l.PassThrough)
                {
                    inner = Fold(l.Children, back.Retain());
                    if (ReferenceEquals(inner, back))
                    {
                        inner.Release();
                        return back;
                    }
                }
                else
                {
                    group = IsolatedGroup(l, coverage);
                    if (group == null)
                    {
                        return back;
                    }
                }
            }
            catch
            {
                back.Release();
                throw;
            }

            return inner != null ? Mix(back, inner, coverage, l.Opacity) : OverHeld(back, group!, l.Opacity, l.Blend, AlphaComposition.SrcOver);
        }
        finally
        {
            if (coverage != null)
            {
                ArrayPool<byte>.Shared.Return(coverage);
            }
        }
    }

    /// <summary>Draws a clipping base with the layers clipped to it into a transparent surface, then lays that surface over the backdrop as one layer with the base's blend mode and opacity.</summary>
    private Tile ApplyClipRun(LayerState[] layers, int first, int end, Tile back)
    {
        LayerState b = layers[first];
        Tile? surface = null;
        try
        {
            surface = BaseContent(b);
            if (surface == null)
            {
                return back;
            }

            for (int k = first + 1; k < end; k++)
            {
                LayerState c = layers[k];
                if (!c.Visible || c.Opacity <= 0f)
                {
                    continue;
                }

                // Source-atop keeps the surface's own coverage, so a clipped layer can never extend the base's shape.
                // 'surface' is nulled while a callee that consumes it runs, so the catch below never releases it twice.
                switch (c.Kind)
                {
                    case LayerKind.Pixel or LayerKind.Fill:
                        if (Touches(c))
                        {
                            if (c.Mask == null)
                            {
                                Tile s = surface;
                                surface = null;
                                surface = Over(s, c.Node!, c.Bounds.X, c.Bounds.Y, c.Opacity, c.Blend, AlphaComposition.SrcAtop);
                            }
                            else if (MaskedContent(c) is { } content)
                            {
                                Tile s = surface;
                                surface = null;
                                surface = OverHeld(s, content, c.Opacity, c.Blend, AlphaComposition.SrcAtop);
                            }
                        }

                        break;
                    case LayerKind.Group:
                        if (c.Children.Length > 0)
                        {
                            byte[]? cov = c.Mask != null ? Coverage(c.Mask, out _, out _) : null;
                            try
                            {
                                Tile? g = IsolatedGroup(c, cov);
                                if (g != null)
                                {
                                    Tile s = surface;
                                    surface = null;
                                    surface = OverHeld(s, g, c.Opacity, c.Blend, AlphaComposition.SrcAtop);
                                }
                            }
                            finally
                            {
                                if (cov != null)
                                {
                                    ArrayPool<byte>.Shared.Return(cov);
                                }
                            }
                        }

                        break;
                    default:
                        {
                            Tile s = surface;
                            surface = null;
                            surface = ApplyAdjustment(c, s);
                            break;
                        }
                }
            }
        }
        catch
        {
            surface?.Release();
            back.Release();
            throw;
        }

        return OverHeld(back, surface, b.Opacity, b.Blend, AlphaComposition.SrcOver);
    }

    private Tile? BaseContent(LayerState b)
    {
        if (b.Kind == LayerKind.Group)
        {
            if (b.Children.Length == 0)
            {
                return null;
            }

            byte[]? cov = null;
            try
            {
                if (b.Mask != null)
                {
                    cov = Coverage(b.Mask, out bool zero, out _);
                    if (zero)
                    {
                        return null;
                    }
                }

                return IsolatedGroup(b, cov);
            }
            finally
            {
                if (cov != null)
                {
                    ArrayPool<byte>.Shared.Return(cov);
                }
            }
        }

        if (b.Kind == LayerKind.Adjustment || !Touches(b))
        {
            return null;
        }

        return MaskedContent(b);
    }

    /// <summary>The content of a group drawn over a transparent surface and scaled by the mask coverage; null when nothing was drawn.</summary>
    private Tile? IsolatedGroup(LayerState g, byte[]? coverage)
    {
        Tile surface = Fold(g.Children, _zero.Retain());
        if (ReferenceEquals(surface, _zero))
        {
            surface.Release();
            return null;
        }

        return coverage == null ? surface : Scale(surface, coverage);
    }

    /// <summary>A pixel or fill layer's pixels placed on a transparent tile and scaled by its mask (if any); null when nothing is visible.</summary>
    private Tile? MaskedContent(LayerState l)
    {
        byte[]? cov = null;
        try
        {
            Tile placed = Over(_zero.Retain(), l.Node!, l.Bounds.X, l.Bounds.Y, 1f, BlendMode.Normal, AlphaComposition.SrcOver);
            try
            {
                if (ReferenceEquals(placed, _zero))
                {
                    placed.Release();
                    return null;
                }

                if (l.Mask == null)
                {
                    return placed;
                }

                cov = Coverage(l.Mask, out bool zero, out bool full);
                if (zero)
                {
                    placed.Release();
                    return null;
                }

                if (full)
                {
                    return placed;
                }
            }
            catch
            {
                placed.Release();
                throw;
            }

            return Scale(placed, cov);
        }
        finally
        {
            if (cov != null)
            {
                ArrayPool<byte>.Shared.Return(cov);
            }
        }
    }

    /// <summary>True when the layer can change this tile: its bounds overlap it and, for a pixel layer, some tile under it is not empty.</summary>
    private bool Touches(LayerState l)
    {
        ImageRect hit = l.Bounds.Intersect(_rect);
        if (hit.IsEmpty)
        {
            return false;
        }

        TileGrid? g = l.Grid;
        if (g == null || g.Backing != null)
        {
            return true;
        }

        int lx0 = (hit.X - l.Bounds.X) >> Tile.Shift;
        int lx1 = (hit.Right - 1 - l.Bounds.X) >> Tile.Shift;
        int ly0 = (hit.Y - l.Bounds.Y) >> Tile.Shift;
        int ly1 = (hit.Bottom - 1 - l.Bounds.Y) >> Tile.Shift;
        for (int ty = ly0; ty <= ly1; ty++)
        {
            for (int tx = lx0; tx <= lx1; tx++)
            {
                if (g.Tiles[(ty * g.TilesX) + tx] != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Lays an overlay node (whose top-left corner is at document (x, y)) over the backdrop tile, as <c>Image.Composite</c> would.</summary>
    private Tile Over(Tile back, Node overlay, int x, int y, float opacity, BlendMode blend, AlphaComposition composition)
    {
        try
        {
            var held = new HeldTileNode(back, _rect, _format);
            Tile result;
            if (_doc.Linear)
            {
                Image b = Image.FromNode(held).WithMetadata(_doc.Metadata);
                Image o = Image.FromNode(overlay).WithMetadata(_doc.Metadata);
                result = b.Composite(o, x - _rect.X, y - _rect.Y, opacity, linear: true, blend, composition).Root.GetTile(0, 0, _context, useCache: false);
            }
            else
            {
                var node = new CompositeNode(held, overlay, x - _rect.X, y - _rect.Y, opacity, blend, composition) { Ephemeral = true };
                result = node.GetTile(0, 0, _context);
            }

            back.Release();
            return result;
        }
        catch
        {
            back.Release();
            throw;
        }
    }

    private Tile OverHeld(Tile back, Tile overlay, float opacity, BlendMode blend, AlphaComposition composition)
    {
        try
        {
            return Over(back, new HeldTileNode(overlay, _rect, _format), _rect.X, _rect.Y, opacity, blend, composition);
        }
        finally
        {
            overlay.Release();
        }
    }

    /// <summary>Applies an adjustment layer's point operation to the backdrop tile.</summary>
    private Tile Adjusted(Adjustment adjustment, Tile back)
    {
        Image source = Image.FromNode(new HeldTileNode(back, _rect, _format)).WithMetadata(_doc.Metadata);
        Image result = adjustment.Apply(source);
        if (result.Width != _rect.Width || result.Height != _rect.Height || result.Format != _format)
        {
            throw new ImageOperationException($"The adjustment '{adjustment.Name}' changed the size or format of its input; adjustments must be point operations.");
        }

        return result.Root.GetTile(0, 0, _context, useCache: false);
    }

    /// <summary>
    /// Reads the mask coverage for this tile into a rented 8-bit array (row stride = tile width): the mask where its canvas lies, its outside value elsewhere.
    /// The caller returns the array to <see cref="ArrayPool{T}"/>.
    /// </summary>
    private byte[] Coverage(MaskState mask, out bool allZero, out bool allFull)
    {
        int w = _rect.Width;
        int h = _rect.Height;
        byte[] cov = ArrayPool<byte>.Shared.Rent(w * h);
        try
        {
            cov.AsSpan(0, w * h).Fill(mask.Outside);
            ImageRect hit = mask.Area.Intersect(_rect);
            if (!hit.IsEmpty)
            {
                byte[] scratch = ArrayPool<byte>.Shared.Rent(hit.Width * hit.Height);
                try
                {
                    Render.ReadRegion(mask.Node, new ImageRect(hit.X - mask.Area.X, hit.Y - mask.Area.Y, hit.Width, hit.Height), scratch, hit.Width, PixelFormat.Gray8, _context);
                    for (int r = 0; r < hit.Height; r++)
                    {
                        scratch.AsSpan(r * hit.Width, hit.Width).CopyTo(cov.AsSpan((((hit.Y - _rect.Y) + r) * w) + (hit.X - _rect.X)));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
            }

            byte min = 255;
            byte max = 0;
            foreach (byte v in cov.AsSpan(0, w * h))
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            allZero = max == 0;
            allFull = min == 255;
            return cov;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(cov);
            throw;
        }
    }

    /// <summary>Scales the alpha of a tile by coverage/255 (rounded half up); the colors, being straight, stay. Consumes <paramref name="tile"/>.</summary>
    private Tile Scale(Tile tile, byte[] coverage)
    {
        Tile? result = null;
        try
        {
            result = Tile.Rent(tile.Length, clear: false);
            int w = _rect.Width;
            int bpp = _format.BytesPerPixel;
            int bps = _format.BytesPerSample;
            int alphaAt = (_format.ChannelCount - 1) * bps;
            int stride = Tile.Size * bpp;
            ReadOnlySpan<byte> src = tile.Span;
            Span<byte> dst = result.Span;
            for (int y = 0; y < _rect.Height; y++)
            {
                src.Slice(y * stride, w * bpp).CopyTo(dst.Slice(y * stride, w * bpp));
                Span<byte> row = dst.Slice(y * stride, w * bpp);
                ReadOnlySpan<byte> cov = coverage.AsSpan(y * w, w);
                for (int x = 0; x < w; x++)
                {
                    int at = (x * bpp) + alphaAt;
                    int k = cov[x];
                    if (k == 255)
                    {
                        continue;
                    }

                    switch (_format.Sample)
                    {
                        case SampleType.U8:
                            row[at] = (byte)(((row[at] * k) + 127) / 255);
                            break;
                        case SampleType.U16:
                            {
                                uint a = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(at, 2));
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(at, 2), (ushort)(((a * (uint)k) + 127) / 255));
                                break;
                            }

                        default:
                            {
                                float a = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(row.Slice(at, 4));
                                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(row.Slice(at, 4), a * (k / 255f));
                                break;
                            }
                    }
                }
            }

            tile.Release();
            return result;
        }
        catch
        {
            result?.Release();
            tile.Release();
            throw;
        }
    }

    /// <summary>
    /// <c>back + k·(target − back)</c> on premultiplied float values, with <c>k = opacity · coverage/255</c>. Consumes both tiles. With k = 1 everywhere the result is
    /// <paramref name="target"/> itself, so a full-strength group or adjustment stays bit-identical to its contents.
    /// </summary>
    private Tile Mix(Tile back, Tile target, byte[]? coverage, float opacity)
    {
        Tile? result = null;
        float[]? fb = null;
        float[]? ft = null;
        try
        {
            if (opacity >= 1f && coverage == null)
            {
                back.Release();
                return target;
            }

            int w = _rect.Width;
            int bpp = _format.BytesPerPixel;
            int channels = _work.ChannelCount;
            int stride = Tile.Size * bpp;
            fb = ArrayPool<float>.Shared.Rent(w * channels);
            ft = ArrayPool<float>.Shared.Rent(w * channels);
            result = Tile.Rent(back.Length, clear: false);
            back.Span.CopyTo(result.Span);
            ReadOnlySpan<byte> bs = back.Span;
            ReadOnlySpan<byte> ts = target.Span;
            Span<byte> rs = result.Span;
            for (int y = 0; y < _rect.Height; y++)
            {
                ConvertKernel.ConvertRow(bs.Slice(y * stride, w * bpp), _format, SpanCast.AsBytes(fb.AsSpan(0, w * channels)), _work, w);
                ConvertKernel.ConvertRow(ts.Slice(y * stride, w * bpp), _format, SpanCast.AsBytes(ft.AsSpan(0, w * channels)), _work, w);
                for (int x = 0; x < w; x++)
                {
                    float k = opacity * (coverage == null ? 1f : coverage[(y * w) + x] / 255f);
                    for (int c = 0; c < channels; c++)
                    {
                        int i = (x * channels) + c;
                        fb[i] += k * (ft[i] - fb[i]);
                    }
                }

                ConvertKernel.ConvertRow(SpanCast.AsBytes(fb.AsSpan(0, w * channels)), _work, rs.Slice(y * stride, w * bpp), _format, w);
            }

            back.Release();
            target.Release();
            return result;
        }
        catch
        {
            result?.Release();
            back.Release();
            target.Release();
            throw;
        }
        finally
        {
            if (fb != null)
            {
                ArrayPool<float>.Shared.Return(fb);
            }

            if (ft != null)
            {
                ArrayPool<float>.Shared.Return(ft);
            }
        }
    }
}
