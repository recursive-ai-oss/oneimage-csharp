using System.Buffers;

namespace OneImage;

/// <summary>
/// Mosaic: the image is cut into <c>size</c> × <c>size</c> blocks from the top-left corner and every block takes the value of
/// the one pixel at its center (block start + size/2, limited to the last pixel for a partial block at the right or bottom
/// edge). It is a pure copy of whole pixels, so it works for every pixel format. An output tile reads only the center pixels
/// of the blocks it touches.
/// </summary>
internal sealed class PixelateNode : Node
{
    private readonly int _size;

    public PixelateNode(Node input, int size)
        : base(input.Width, input.Height, input.Format, new OpDescriptor("pixelate", new OpParameter("size", (long)size)), input)
    {
        _size = size;
    }

    private int Center(int i, int limit) => Math.Min(((i / _size) * _size) + (_size / 2), limit - 1);

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        Node input = Inputs[0];
        int x0 = Center(output.X, input.Width);
        int x1 = Center(output.Right - 1, input.Width);
        int y0 = Center(output.Y, input.Height);
        int y1 = Center(output.Bottom - 1, input.Height);
        return new ImageRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        ImageRect need = RequiredInput(0, o);
        int bpp = Format.BytesPerPixel;
        byte[] src = ArrayPool<byte>.Shared.Rent(need.Width * need.Height * bpp);
        int[] cols = ArrayPool<int>.Shared.Rent(o.Width);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, need, src, need.Width * bpp, input.Format, context);
            for (int x = 0; x < o.Width; x++)
            {
                cols[x] = (Center(o.X + x, input.Width) - need.X) * bpp;
            }

            int previous = -1;
            for (int y = 0; y < o.Height; y++)
            {
                int sy = Center(o.Y + y, input.Height) - need.Y;
                Span<byte> dst = tile.Span.Slice(y * Tile.Size * bpp, o.Width * bpp);
                if (sy == previous)
                {
                    // Rows of one block are identical: copy the row just built.
                    tile.Span.Slice((y - 1) * Tile.Size * bpp, o.Width * bpp).CopyTo(dst);
                    continue;
                }

                previous = sy;
                ReadOnlySpan<byte> row = src.AsSpan(sy * need.Width * bpp, need.Width * bpp);
                for (int x = 0; x < o.Width; x++)
                {
                    row.Slice(cols[x], bpp).CopyTo(dst.Slice(x * bpp, bpp));
                }
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
            ArrayPool<byte>.Shared.Return(src);
            ArrayPool<int>.Shared.Return(cols);
        }
    }
}
