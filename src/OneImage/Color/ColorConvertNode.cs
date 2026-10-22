namespace OneImage;

/// <summary>Converts every tile through a <see cref="ColorTransform"/> (profile to profile conversion, alpha preserved).</summary>
internal sealed class ColorConvertNode : Node, IRowStage
{
    private readonly ColorTransform _transform;

    public ColorConvertNode(Node input, ColorTransform transform, PixelFormat format, OpDescriptor descriptor)
        : base(input.Width, input.Height, format, descriptor, input)
    {
        _transform = transform;
    }

    PixelFormat IRowStage.OutputFormat => Format;

    void IRowStage.ConvertRow(ReadOnlySpan<byte> source, Span<byte> destination, int width) =>
        _transform.ConvertRow(source, Inputs[0].Format, destination, Format, width);

    /// <summary>A one-shot whole-frame read of a decode converts rows as the decoder delivers them, so neither the decoded frame nor a streaming window is buffered.</summary>
    public override bool TryReadDirect(Span<byte> destination, PixelFormat destinationFormat, int degree, CancellationToken cancellationToken) =>
        Inputs[0] is DecodeSourceNode decode && decode.TryDecodeInto(destination, destinationFormat, this, degree, cancellationToken);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Tile src = Inputs[0].GetTile(tx, ty, context);
        try
        {
            Tile dst = Tile.Rent(Tile.ByteSize(Format), clear: false);
            try
            {
                ImageRect r = TileRect(tx, ty);
                PixelFormat sf = Inputs[0].Format;
                int sbpp = sf.BytesPerPixel;
                int dbpp = Format.BytesPerPixel;
                for (int row = 0; row < r.Height; row++)
                {
                    _transform.ConvertRow(
                        src.Span.Slice(row * Tile.Size * sbpp, r.Width * sbpp),
                        sf,
                        dst.Span.Slice(row * Tile.Size * dbpp, r.Width * dbpp),
                        Format,
                        r.Width);
                }

                return dst;
            }
            catch
            {
                dst.Release();
                throw;
            }
        }
        finally
        {
            src.Release();
        }
    }
}
