using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Layers;

internal sealed partial class TileJob
{
    // A layer with effects on its pixels: the effects under it, then its pixels with the other effects worked into them, then that surface over
    // the backdrop with the layer's blend mode and opacity. Working the effects into the surface (instead of drawing them over the result) keeps an
    // antialiased edge pixel from being covered twice.
    private Tile ApplyStyled(LayerState l, Tile back)
    {
        FxPlan plan = l.Fx!;
        back = ApplyFx(l, plan.Below, back);
        Tile? surface;
        try
        {
            if (!Touches(l))
            {
                return back;
            }

            Tile? content = MaskedContent(l);
            if (content == null)
            {
                return back;
            }

            surface = Shade(l, plan, content);
        }
        catch
        {
            back.Release();
            throw;
        }

        return OverHeld(back, surface, plan.Opacity, l.Blend, AlphaComposition.SrcOver);
    }

    // Consumes content. The surface is the layer's pixels (scaled by the fill opacity) with each effect that sits on them mixed in.
    private Tile Shade(LayerState l, FxPlan plan, Tile content)
    {
        int w = _rect.Width;
        int h = _rect.Height;
        int channels = _work.ChannelCount;
        int colors = channels - 1;
        int bpp = _format.BytesPerPixel;
        int stride = Tile.Size * bpp;
        float fill = plan.FillOpacity;
        float[] surface = Pools<float>.Pool.Rent(w * h * channels);
        float[] cov = Pools<float>.Pool.Rent(w * h);
        float[] shape = Pools<float>.Pool.Rent(w * h);
        float[] over = Pools<float>.Pool.Rent(w * channels);
        float[]? paint = null;
        Tile? result = null;
        try
        {
            ReadOnlySpan<byte> src = content.Span;
            for (int y = 0; y < h; y++)
            {
                Span<float> row = surface.AsSpan(y * w * channels, w * channels);
                ConvertKernel.ConvertRow(src.Slice(y * stride, w * bpp), _format, SpanCast.AsBytes(row), _work, w);
                if (fill != 1f)
                {
                    for (int i = 0; i < row.Length; i++)
                    {
                        row[i] *= fill;
                    }
                }
            }

            var source = new FxSource(this, l);
            source.ReadAlpha(_rect, shape);
            bool atop = fill >= 1f && l.Kind != LayerKind.Fill && !plan.ShapeFromMask;
            foreach (FxItem item in plan.Above)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                LayerEffect e = item.Effect;
                float eo = item.Opacity;
                if (eo <= 0f || !EffectRenderer.Render(item, source, _rect, cov))
                {
                    continue;
                }

                // Effects with more than one color (gradients, patterns) bring a color per pixel.
                paint ??= Pools<float>.Pool.Rent(w * h * 4);
                bool painted = EffectRenderer.Paint(item, source, _rect, cov, paint);
                Rgba32 color = item.Color;
                float r = color.R / 255f;
                float g = color.G / 255f;
                float b = color.B / 255f;
                bool recolor = item.Interior == FxInterior.Recolor;
                AlphaComposition composition = recolor || atop ? AlphaComposition.SrcAtop : AlphaComposition.SrcOver;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float c = cov[(y * w) + x];
                        float a = shape[(y * w) + x];
                        float k;
                        if (recolor)
                        {
                            k = a > 0f ? Math.Min(1f, c / a) * eo : 0f;
                        }
                        else if (atop)
                        {
                            k = a > 0f ? Math.Min(1f, c * eo / a) : 0f;
                        }
                        else
                        {
                            k = c * eo;
                        }

                        float pr = r, pg = g, pb = b;
                        if (painted)
                        {
                            int pi = ((y * w) + x) * 4;
                            pr = paint![pi];
                            pg = paint[pi + 1];
                            pb = paint[pi + 2];
                            k *= paint[pi + 3];
                        }

                        int i = x * channels;
                        if (colors == 3)
                        {
                            over[i] = pr * k;
                            over[i + 1] = pg * k;
                            over[i + 2] = pb * k;
                        }
                        else
                        {
                            over[i] = ((0.2126f * pr) + (0.7152f * pg) + (0.0722f * pb)) * k;
                        }

                        over[i + colors] = k;
                    }

                    Span<float> row = surface.AsSpan(y * w * channels, w * channels);
                    BlendKernel.Row(item.Blend, row, over.AsSpan(0, w * channels), row, colors, 1f, composition);
                }
            }

            result = Tile.Rent(Tile.ByteSize(_format), clear: true);
            Span<byte> dst = result.Span;
            for (int y = 0; y < h; y++)
            {
                ConvertKernel.ConvertRow(SpanCast.AsBytes(surface.AsSpan(y * w * channels, w * channels)), _work, dst.Slice(y * stride, w * bpp), _format, w);
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
            content.Release();
            Pools<float>.Pool.Return(surface);
            Pools<float>.Pool.Return(cov);
            Pools<float>.Pool.Return(shape);
            Pools<float>.Pool.Return(over);
            if (paint != null)
            {
                Pools<float>.Pool.Return(paint);
            }
        }
    }

    // Draws effects over the backdrop. Each is a color with a coverage plane, blended with its own mode and its opacity times the layer's.
    private Tile ApplyFx(LayerState l, FxItem[] items, Tile back, AlphaComposition composition = AlphaComposition.SrcOver)
    {
        if (items.Length == 0)
        {
            return back;
        }

        FxPlan plan = l.Fx!;
        if (l.Node == null || Layer.Widen(l.Bounds, plan.Reach).Intersect(_rect).IsEmpty)
        {
            return back;
        }

        var source = new FxSource(this, l);
        int n = _rect.Width * _rect.Height;
        float[] cov = Pools<float>.Pool.Rent(n);
        float[]? paint = null;
        try
        {
            foreach (FxItem item in items)
            {
                float opacity = item.Opacity * plan.Opacity * (item.Interior == FxInterior.Recolor ? plan.FillOpacity : 1f);
                if (opacity <= 0f)
                {
                    continue;
                }

                Tile tile;
                try
                {
                    _context.CancellationToken.ThrowIfCancellationRequested();
                    if (!EffectRenderer.Render(item, source, _rect, cov))
                    {
                        continue;
                    }

                    paint ??= Pools<float>.Pool.Rent(n * 4);
                    tile = EffectTile(item.Color, cov, EffectRenderer.Paint(item, source, _rect, cov, paint) ? paint : null);
                }
                catch
                {
                    back.Release();
                    throw;
                }

                back = OverHeld(back, tile, opacity, item.Blend, composition);
            }
        }
        finally
        {
            Pools<float>.Pool.Return(cov);
            if (paint != null)
            {
                Pools<float>.Pool.Return(paint);
            }
        }

        return back;
    }

    // A tile of one color whose alpha is the coverage.
    private Tile EffectTile(Rgba32 color, float[] cov, float[]? paint = null)
    {
        int w = _rect.Width;
        int bpp = _format.BytesPerPixel;
        int channels = _work.ChannelCount;
        int stride = Tile.Size * bpp;
        float[] row = Pools<float>.Pool.Rent(w * channels);
        Tile? tile = null;
        try
        {
            tile = Tile.Rent(Tile.ByteSize(_format), clear: true);
            float r = color.R / 255f;
            float g = color.G / 255f;
            float b = color.B / 255f;
            float gray = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
            Span<byte> dst = tile.Span;
            for (int y = 0; y < _rect.Height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float a = cov[(y * w) + x];
                    float pr = r, pg = g, pb = b, pgray = gray;
                    if (paint != null)
                    {
                        int pi = ((y * w) + x) * 4;
                        pr = paint[pi];
                        pg = paint[pi + 1];
                        pb = paint[pi + 2];
                        a *= paint[pi + 3];
                        pgray = (0.2126f * pr) + (0.7152f * pg) + (0.0722f * pb);
                    }

                    int i = x * channels;
                    if (channels == 4)
                    {
                        row[i] = pr * a;
                        row[i + 1] = pg * a;
                        row[i + 2] = pb * a;
                        row[i + 3] = a;
                    }
                    else
                    {
                        row[i] = pgray * a;
                        row[i + 1] = a;
                    }
                }

                ConvertKernel.ConvertRow(SpanCast.AsBytes(row.AsSpan(0, w * channels)), _work, dst.Slice(y * stride, w * bpp), _format, w);
            }

            return tile;
        }
        catch
        {
            tile?.Release();
            throw;
        }
        finally
        {
            Pools<float>.Pool.Return(row);
        }
    }

    // The layer's shape: the alpha of its pixels times its mask, read for any rectangle of the document.
    private sealed class FxSource(TileJob job, LayerState layer) : IFxSource
    {
        public ImageRect Box => layer.Fx?.AlignBox ?? layer.Bounds;

        public ImageRect Document => new(0, 0, job._doc.Width, job._doc.Height);

        public void ReadAlpha(ImageRect region, float[] dest)
        {
            int n = region.Width * region.Height;
            Array.Clear(dest, 0, n);
            // A fill layer's shape is its mask (or everywhere): the transparency inside a gradient is part of the fill, not of the shape. A shape layer
            // with the same flag behaves alike. Both read the mask over the whole region, also past the canvas.
            bool maskOnly = layer.Kind == LayerKind.Fill || (layer.Fx!.ShapeFromMask && layer.Mask != null);
            if (maskOnly)
            {
                if (layer.Mask == null)
                {
                    Array.Fill(dest, 1f, 0, n);
                    return;
                }

                byte[] cov = job.Coverage(layer.Mask, region, out _, out _);
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        dest[i] = cov[i] / 255f;
                    }
                }
                finally
                {
                    Pools<byte>.Pool.Return(cov);
                }

                return;
            }

            ImageRect hit = region.Intersect(layer.Bounds);
            if (hit.IsEmpty || layer.Node == null)
            {
                return;
            }

            int channels = job._work.ChannelCount;
            int hn = hit.Width * hit.Height;
            byte[] bytes = Pools<byte>.Pool.Rent(hn * channels * sizeof(float));
            float[] samples = Pools<float>.Pool.Rent(hn * channels);
            byte[]? mask = null;
            try
            {
                Render.ReadRegion(layer.Node, new ImageRect(hit.X - layer.Bounds.X, hit.Y - layer.Bounds.Y, hit.Width, hit.Height), bytes, 0, job._work, job._context);
                Buffer.BlockCopy(bytes, 0, samples, 0, hn * channels * sizeof(float));
                if (layer.Mask != null)
                {
                    mask = job.Coverage(layer.Mask, region, out bool zero, out _);
                    if (zero)
                    {
                        return;
                    }
                }

                for (int y = 0; y < hit.Height; y++)
                {
                    int d = ((hit.Y - region.Y + y) * region.Width) + (hit.X - region.X);
                    for (int x = 0; x < hit.Width; x++)
                    {
                        float a = samples[((y * hit.Width) + x) * channels + (channels - 1)];
                        if (mask != null)
                        {
                            a *= mask[d + x] / 255f;
                        }

                        dest[d + x] = a;
                    }
                }
            }
            finally
            {
                Pools<byte>.Pool.Return(bytes);
                Pools<float>.Pool.Return(samples);
                if (mask != null)
                {
                    Pools<byte>.Pool.Return(mask);
                }
            }
        }
    }
}
