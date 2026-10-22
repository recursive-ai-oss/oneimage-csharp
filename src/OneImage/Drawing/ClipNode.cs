using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Drawing;

/// <summary>
/// Keeps the original pixels outside a path and the processed pixels inside it, blending by the path's coverage along the edge.
/// A tile the path cannot reach is the original's own tile; a tile wholly inside is the processed tile; the processed input is
/// not even asked for tiles outside the path.
/// </summary>
internal sealed class ClipNode : Node
{
    private readonly RasterSource _source;
    private readonly bool _antialias;
    private readonly IntersectionRule _rule;
    private readonly PixelFormat _work;

    public ClipNode(Node original, Node processed, RasterSource source, bool antialias, IntersectionRule rule, OpDescriptor descriptor)
        : base(original.Width, original.Height, original.Format, descriptor, original, processed)
    {
        _source = source;
        _antialias = antialias;
        _rule = rule;
        _work = new PixelFormat(original.Format.Model, SampleType.F32, AlphaMode.Premultiplied);
    }

    /// <summary>The path's raster, which says which tiles can differ from the original.</summary>
    public Raster Raster => _source.Raster;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Raster raster = _source.Raster;
        if (!raster.MayTouch(tx, ty))
        {
            return Inputs[0].GetTile(tx, ty, context);
        }

        ImageRect rect = TileRect(tx, ty);
        RasterScratch scratch = RasterScratch.Rent();
        try
        {
            if (!TileRasterizer.Rasterize(raster, tx, ty, rect.Width, rect.Height, scratch, _antialias, _rule))
            {
                return Inputs[0].GetTile(tx, ty, context);
            }

            if (IsFull(scratch, rect))
            {
                return Inputs[1].GetTile(tx, ty, context);
            }

            Tile original = Inputs[0].GetTile(tx, ty, context);
            Tile processed;
            try
            {
                processed = Inputs[1].GetTile(tx, ty, context);
            }
            catch
            {
                original.Release();
                throw;
            }

            Tile result = Tile.Rent(original.Length, clear: false);
            try
            {
                original.Span.CopyTo(result.Span);
                Blend(result.Span, processed.Span, rect, scratch);
                return result;
            }
            catch
            {
                result.Release();
                throw;
            }
            finally
            {
                original.Release();
                processed.Release();
            }
        }
        finally
        {
            RasterScratch.Return(scratch);
        }
    }

    private static bool IsFull(RasterScratch s, ImageRect rect)
    {
        for (int r = 0; r < rect.Height; r++)
        {
            if (s.SpanStart[r] != 0 || s.SpanEnd[r] != rect.Width)
            {
                return false;
            }

            ReadOnlySpan<float> cov = s.Coverage.AsSpan(r * Tile.Size, rect.Width);
            foreach (float c in cov)
            {
                if (c < 1f)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Pixels fully inside take the processed bytes exactly; partly covered ones are mixed in premultiplied float, which is how
    // a layer mask mixes, and rounded back to the format.
    private void Blend(Span<byte> result, ReadOnlySpan<byte> processed, ImageRect rect, RasterScratch s)
    {
        int bpp = Format.BytesPerPixel;
        int ch = _work.ChannelCount;
        float[] buffer = ArrayPool<float>.Shared.Rent(3 * Tile.Size * ch);
        try
        {
            for (int r = 0; r < rect.Height; r++)
            {
                int a = s.SpanStart[r], b = s.SpanEnd[r];
                int x = a;
                ReadOnlySpan<float> cov = s.Coverage.AsSpan(r * Tile.Size, Tile.Size);
                int rowStart = r * Tile.Size * bpp;
                while (x < b)
                {
                    float c = cov[x];
                    if (c <= 0f)
                    {
                        x++;
                        continue;
                    }

                    int e = x + 1;
                    if (c >= 1f)
                    {
                        while (e < b && cov[e] >= 1f)
                        {
                            e++;
                        }

                        processed.Slice(rowStart + (x * bpp), (e - x) * bpp).CopyTo(result.Slice(rowStart + (x * bpp), (e - x) * bpp));
                    }
                    else
                    {
                        while (e < b && cov[e] > 0f && cov[e] < 1f)
                        {
                            e++;
                        }

                        MixRun(result.Slice(rowStart + (x * bpp), (e - x) * bpp), processed.Slice(rowStart + (x * bpp), (e - x) * bpp), cov.Slice(x, e - x), buffer, ch);
                    }

                    x = e;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private void MixRun(Span<byte> pixels, ReadOnlySpan<byte> processed, ReadOnlySpan<float> cov, float[] buffer, int ch)
    {
        int n = cov.Length;
        Span<float> back = buffer.AsSpan(0, n * ch), over = buffer.AsSpan(Tile.Size * ch, n * ch), mixed = buffer.AsSpan(2 * Tile.Size * ch, n * ch);
        ConvertKernel.ConvertRow(pixels, Format, SpanCast.AsBytes(back), _work, n);
        ConvertKernel.ConvertRow(processed, Format, SpanCast.AsBytes(over), _work, n);
        for (int i = 0; i < n; i++)
        {
            float c = cov[i];
            for (int k = 0; k < ch; k++)
            {
                int j = (i * ch) + k;
                mixed[j] = back[j] + ((over[j] - back[j]) * c);
            }
        }

        ConvertKernel.ConvertRow(SpanCast.AsBytes(mixed), _work, pixels, Format, n);
    }

    /// <summary>Describes a clip for an op descriptor.</summary>
    public static OpDescriptor Describe(Path path, System.Numerics.Matrix3x2 m, bool antialias, IntersectionRule rule) =>
        new("clip", () =>
        [
            new("rule", rule == IntersectionRule.NonZero ? "nonzero" : "evenodd"),
            new("antialias", antialias),
            new("m11", (double)m.M11),
            new("m12", (double)m.M12),
            new("m21", (double)m.M21),
            new("m22", (double)m.M22),
            new("m31", (double)m.M31),
            new("m32", (double)m.M32),
            new("path", FillNode.DescribePath(path)),
        ]);
}
