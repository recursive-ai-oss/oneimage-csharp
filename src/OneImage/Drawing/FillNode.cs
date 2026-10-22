using System.Globalization;
using System.Numerics;

namespace OneImage.Drawing;

/// <summary>Names a fill as another op (a stroke) in descriptors: the op name, the path to show, and extra parameters.</summary>
internal sealed record FillLabel(string Name, Path Path, OpParameter[] Extra);

/// <summary>A path, its transform and the image size it will be rasterized for; builds the raster once, on first use, for any number of threads.</summary>
internal sealed class RasterSource
{
    private readonly Lazy<Raster> _raster;

    public RasterSource(Path path, Matrix3x2 transform, int width, int height, double tolerance) =>
        _raster = new Lazy<Raster>(() => Raster.Build(path, transform, width, height, tolerance), LazyThreadSafetyMode.ExecutionAndPublication);

    public Raster Raster => _raster.Value;
}

/// <summary>
/// Fills a path with a brush over its input. An output tile that the path cannot touch is the input's own tile; a tile it can
/// touch is rasterized first, and only if some pixel has coverage is the input copied and painted.
/// </summary>
internal sealed class FillNode : Node
{
    private readonly RasterSource _source;
    private readonly FillPlan _plan;

    public FillNode(Node input, RasterSource source, FillPlan plan, OpDescriptor descriptor)
        : base(input.Width, input.Height, input.Format, descriptor, input)
    {
        _source = source;
        _plan = plan;
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        // A run of fills on top of each other (a drawing built shape by shape) is computed from the tile under the whole run:
        // painting the layers in order on one copy gives the same bytes as a node per layer would, without a tile per layer.
        RasterScratch scratch = RasterScratch.Rent();
        List<FillNode> layers = scratch.Layers;
        layers.Clear();
        Node source = this;
        while (source is FillNode f)
        {
            layers.Add(f);
            source = f.Inputs[0];
        }

        layers.Reverse();
        Tile back;
        try
        {
            back = source.GetTile(tx, ty, context);
        }
        catch
        {
            layers.Clear();
            RasterScratch.Return(scratch);
            throw;
        }

        Tile? result = null;
        try
        {
            ImageRect rect = TileRect(tx, ty);
            foreach (FillNode layer in layers)
            {
                if (!layer._source.Raster.MayTouch(tx, ty))
                {
                    continue;
                }

                FillSettings st = layer._plan.Settings;
                if (!TileRasterizer.Rasterize(layer._source.Raster, tx, ty, rect.Width, rect.Height, scratch, st.Antialias, st.Rule))
                {
                    continue;
                }

                if (result == null)
                {
                    result = Tile.Rent(back.Length, clear: false);
                    back.Span.CopyTo(result.Span);
                }

                layer._plan.Paint(result.Span, rect.Width, rect.Height, rect.X, rect.Y, scratch);
            }

            if (result == null)
            {
                return back;
            }

            Tile done = result;
            result = null;
            back.Release();
            return done;
        }
        catch
        {
            result?.Release();
            back.Release();
            throw;
        }
        finally
        {
            layers.Clear();
            RasterScratch.Return(scratch);
        }
    }

    /// <summary>Describes a fill for an op descriptor.</summary>
    public static OpDescriptor Describe(string name, Brush brush, Path path, FillSettings s, FillLabel? label = null)
    {
        if (label != null)
        {
            name = label.Name + name.AsSpan(4).ToString();
            return new OpDescriptor(name, () => Parameters(brush, label.Path, s, label.Extra));
        }

        return new OpDescriptor(name, () => Parameters(brush, path, s, null));
    }

    private static OpParameter[] Parameters(Brush brush, Path path, FillSettings s, OpParameter[]? extra)
    {
        var p = new List<OpParameter>(brush.Describe())
        {
            new("rule", s.Rule == IntersectionRule.NonZero ? "nonzero" : "evenodd"),
            new("antialias", s.Antialias),
            new("opacity", (double)s.Opacity),
            new("mode", BlendNames.Name(s.Blend) ?? "normal"),
            new("alpha", BlendNames.Name(s.Composition)),
            new("linear", s.Linear),
            new("m11", (double)s.Transform.M11),
            new("m12", (double)s.Transform.M12),
            new("m21", (double)s.Transform.M21),
            new("m22", (double)s.Transform.M22),
            new("m31", (double)s.Transform.M31),
            new("m32", (double)s.Transform.M32),
            new("path", DescribePath(path)),
        };
        if (extra != null)
        {
            p.AddRange(extra);
        }

        return [.. p];
    }

    // Small paths are described in full as SVG path data; a big one by its size and a 64-bit FNV-1a hash of its numbers,
    // so building a fill never costs a string as big as the path.
    private static string DescribePath(Path path)
    {
        if (path.Verbs.Length <= 1024)
        {
            return path.ToSvg();
        }

        ulong h = 14695981039346656037UL;
        foreach (byte v in path.Verbs)
        {
            h = (h ^ v) * 1099511628211UL;
        }

        foreach (float f in path.Coords)
        {
            h = (h ^ BitConverter.SingleToUInt32Bits(f)) * 1099511628211UL;
        }

        return string.Create(CultureInfo.InvariantCulture, $"hash:{path.Verbs.Length}:{h:x16}");
    }
}

/// <summary>
/// Takes tiles the path can touch from <c>processed</c> (the fill, run in another space) and every other tile from the
/// untouched base, so pixels the fill cannot reach are never converted back and forth.
/// </summary>
internal sealed class FillSelectNode : Node
{
    private readonly RasterSource _source;

    public FillSelectNode(Node original, Node processed, RasterSource source, OpDescriptor descriptor)
        : base(original.Width, original.Height, original.Format, descriptor, original, processed) => _source = source;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context) =>
        Inputs[_source.Raster.MayTouch(tx, ty) ? 1 : 0].GetTile(tx, ty, context);
}
