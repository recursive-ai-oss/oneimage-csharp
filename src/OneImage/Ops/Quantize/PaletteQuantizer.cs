using System.Buffers;

namespace OneImage.Ops.Quantize;

/// <summary>
/// The palette entry points other codecs use (indexed PNG, GIF). Rows are straight-alpha RGBA8.
/// For streaming callers: feed rows to a <see cref="ColorQuantizer"/>, call <see cref="ColorQuantizer.BuildPalette"/>, then feed the same
/// rows again, top to bottom, through a <see cref="PaletteMapper"/>.
/// </summary>
internal static class PaletteQuantizer
{
    /// <summary>Quantizes a whole RGBA8 buffer.</summary>
    /// <param name="rgba">Tightly packed rows, <c>width * height * 4</c> bytes.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="maxColors">Palette size limit, 2 to 256.</param>
    /// <param name="dither">Dithering; ignored when the image already has few enough colors.</param>
    /// <param name="alpha">Alpha handling.</param>
    /// <param name="palette">Receives the palette.</param>
    /// <returns>One palette index per pixel, row-major.</returns>
    public static byte[] Quantize(ReadOnlySpan<byte> rgba, int width, int height, int maxColors, Dither dither, QuantizeAlpha alpha, out QuantizedPalette palette)
    {
        long pixels = (long)width * height;
        if (width <= 0 || height <= 0 || pixels * 4 > rgba.Length || pixels > Array.MaxLength)
        {
            throw new ArgumentException("The buffer does not match the size.", nameof(rgba));
        }

        var q = new ColorQuantizer(maxColors, alpha);
        q.AddRows(rgba.Slice(0, (int)(pixels * 4)));
        palette = q.BuildPalette();
        var mapper = new PaletteMapper(palette, width, dither);
        var result = new byte[pixels];
        for (int y = 0; y < height; y++)
        {
            mapper.MapRow(y, rgba.Slice(y * width * 4, width * 4), result.AsSpan(y * width, width));
        }

        return result;
    }
}

/// <summary>
/// Graph node for <see cref="Image.Quantize"/>. A palette needs statistics of the whole frame and error diffusion runs in row order,
/// so the first tile request computes the frame in two forward passes over the input (histogram, then map) and keeps the result tiles.
/// </summary>
internal sealed class QuantizeNode : Node
{
    private readonly int _colors;
    private readonly Dither _dither;
    private readonly QuantizeAlpha _alpha;
    private readonly Quantizer _quantizer;
    private readonly object _gate = new();
    private TileGrid? _grid;

    public QuantizeNode(Node input, int colors, Dither dither, QuantizeAlpha alpha, Quantizer quantizer)
        : base(
            input.Width,
            input.Height,
            input.Format.HasAlpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8,
            Describe(colors, dither, alpha, quantizer),
            input)
    {
        _colors = colors;
        _dither = dither;
        _alpha = alpha;
        _quantizer = quantizer;
    }

    // The quantizer is named only when it is not the default, so graphs of the default stay as they were.
    private static OpDescriptor Describe(int colors, Dither dither, QuantizeAlpha alpha, Quantizer quantizer) =>
        quantizer == Quantizer.Wu
            ? new OpDescriptor("quantize", new OpParameter("colors", (long)colors), new OpParameter("dither", dither.ToString()), new OpParameter("alpha", alpha.ToString()))
            : new OpDescriptor("quantize", new OpParameter("colors", (long)colors), new OpParameter("dither", dither.ToString()), new OpParameter("alpha", alpha.ToString()), new OpParameter("quantizer", quantizer.ToString()));

    public override bool Cacheable => false;

    public override bool Streaming => false;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        TileGrid grid = Volatile.Read(ref _grid) ?? Materialize(context);
        return grid.Tiles[(ty * grid.TilesX) + tx]!.Retain();
    }

    private TileGrid Materialize(RenderContext context)
    {
        lock (_gate)
        {
            if (_grid != null)
            {
                return _grid;
            }

            Node input = Inputs[0];
            int w = Width;
            int h = Height;
            int inRow = checked(w * 4);
            int outBpp = Format.BytesPerPixel;
            byte[] band = Pools<byte>.Pool.Rent(checked(inRow * Math.Min(Tile.Size, h)));
            byte[] outBand = Pools<byte>.Pool.Rent(checked(w * outBpp * Math.Min(Tile.Size, h)));
            byte[] idx = Pools<byte>.Pool.Rent(w);
            var grid = new TileGrid(w, h, Format, null);
            try
            {
                var q = new PaletteBuilder(_quantizer, _colors, _alpha);
                for (int y = 0; y < h; y += Tile.Size)
                {
                    int rows = Math.Min(Tile.Size, h - y);
                    Render.ReadRegion(input, new ImageRect(0, y, w, rows), band, inRow, PixelFormat.Rgba8, context);
                    q.AddRows(band.AsSpan(0, rows * inRow));
                }

                QuantizedPalette palette = q.Build();
                var mapper = new PaletteMapper(palette, w, _dither);
                byte[] pal = palette.Rgba;
                using var sink = new StripToTileBuffer(grid);
                for (int y = 0; y < h; y += Tile.Size)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    int rows = Math.Min(Tile.Size, h - y);
                    Render.ReadRegion(input, new ImageRect(0, y, w, rows), band, inRow, PixelFormat.Rgba8, context);
                    for (int r = 0; r < rows; r++)
                    {
                        mapper.MapRow(y + r, band.AsSpan(r * inRow, inRow), idx);
                        Span<byte> dst = outBand.AsSpan(r * w * outBpp, w * outBpp);
                        if (outBpp == 4)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                pal.AsSpan(idx[x] * 4, 4).CopyTo(dst.Slice(x * 4, 4));
                            }
                        }
                        else
                        {
                            for (int x = 0; x < w; x++)
                            {
                                pal.AsSpan(idx[x] * 4, 3).CopyTo(dst.Slice(x * 3, 3));
                            }
                        }
                    }

                    sink.WriteRows(y, outBand.AsSpan(0, rows * w * outBpp), rows);
                }

                Volatile.Write(ref _grid, grid);
                return grid;
            }
            catch
            {
                foreach (Tile? t in grid.Tiles)
                {
                    t?.Release();
                }

                throw;
            }
            finally
            {
                Pools<byte>.Pool.Return(band);
                Pools<byte>.Pool.Return(outBand);
                Pools<byte>.Pool.Return(idx);
            }
        }
    }
}
