using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Crop: the output pixel (x, y) is the input pixel (x + X, y + Y). Only input tiles that intersect the shifted
/// rectangle are ever pulled. When X and Y are multiples of 128, input tiles are reused without copying.
/// </summary>
internal sealed class CropNode : Node
{
    private readonly ImageRect _rect;

    public CropNode(Node input, ImageRect rect)
        : base(
            rect.Width,
            rect.Height,
            input.Format,
            new OpDescriptor("crop", new OpParameter("x", (long)rect.X), new OpParameter("y", (long)rect.Y), new OpParameter("width", (long)rect.Width), new OpParameter("height", (long)rect.Height)),
            input)
    {
        _rect = rect;
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) => output.Offset(_rect.X, _rect.Y);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Node input = Inputs[0];
        if (((_rect.X | _rect.Y) & (Tile.Size - 1)) == 0)
        {
            return input.GetTile(tx + (_rect.X >> Tile.Shift), ty + (_rect.Y >> Tile.Shift), context);
        }

        ImageRect outRect = TileRect(tx, ty);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, RequiredInput(0, outRect), tile.Span, Tile.Size * Format.BytesPerPixel, Format, context);
            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
    }
}

/// <summary>A per-pixel op: each output row depends only on the same row of the input, computed by a registered row kernel.</summary>
internal sealed class RowOpNode : Node
{
    private readonly RowKernel _kernel;

    public RowOpNode(Node input, string opName, OpDescriptor descriptor)
        : base(input.Width, input.Height, input.Format, descriptor, input)
    {
        _kernel = KernelRegistry.Get<RowKernel>(opName);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Tile src = Inputs[0].GetTile(tx, ty, context);
        try
        {
            Tile dst = Tile.Rent(src.Length, clear: false);
            ImageRect r = TileRect(tx, ty);
            int bpp = Format.BytesPerPixel;
            int stride = Tile.Size * bpp;
            for (int row = 0; row < r.Height; row++)
            {
                _kernel(src.Span.Slice(row * stride, r.Width * bpp), dst.Span.Slice(row * stride, r.Width * bpp), Format);
            }

            return dst;
        }
        finally
        {
            src.Release();
        }
    }
}

/// <summary>Converts the pixel format of every tile (see <see cref="ConvertKernel"/> for the rules).</summary>
internal sealed class ConvertNode : Node
{
    private readonly ConvertRowKernel _kernel;

    public ConvertNode(Node input, PixelFormat format)
        : base(
            input.Width,
            input.Height,
            format,
            new OpDescriptor("convert", new OpParameter("model", format.Model.ToString()), new OpParameter("sample", format.Sample.ToString()), new OpParameter("alpha", format.Alpha.ToString())),
            input)
    {
        _kernel = KernelRegistry.Get<ConvertRowKernel>("convert");
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Tile src = Inputs[0].GetTile(tx, ty, context);
        try
        {
            Tile dst = Tile.Rent(Tile.ByteSize(Format), clear: false);
            ImageRect r = TileRect(tx, ty);
            int sbpp = Inputs[0].Format.BytesPerPixel;
            int dbpp = Format.BytesPerPixel;
            for (int row = 0; row < r.Height; row++)
            {
                _kernel(src.Span.Slice(row * Tile.Size * sbpp, r.Width * sbpp), Inputs[0].Format, dst.Span.Slice(row * Tile.Size * dbpp, r.Width * dbpp), Format, r.Width);
            }

            return dst;
        }
        finally
        {
            src.Release();
        }
    }
}
