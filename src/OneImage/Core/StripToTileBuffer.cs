using System.Buffers;

namespace OneImage;

/// <summary>
/// The strip-to-tile line buffer. Sequential decoders deliver rows top to bottom; this collects 128 rows (one tile row)
/// in a pooled band and then splits the band into tiles, so each tile is written in one pass instead of scattering
/// every row across the whole tile row.
/// </summary>
internal sealed class StripToTileBuffer : IRowSink, IDisposable
{
    private readonly ITileRowReceiver _receiver;
    private readonly int _tilesX;
    private readonly int _rowBytes;
    private byte[]? _band;
    private int _nextRow;
    private int _bandRows;

    /// <summary>Creates a buffer filling <paramref name="grid"/> (whose size is the sink size).</summary>
    public StripToTileBuffer(TileGrid grid)
        : this(grid.Width, grid.Height, grid.Format, new GridReceiver(grid))
    {
    }

    /// <summary>Creates a buffer that hands each completed tile row to <paramref name="receiver"/>.</summary>
    public StripToTileBuffer(int width, int height, PixelFormat format, ITileRowReceiver receiver)
    {
        _receiver = receiver;
        _tilesX = (width + Tile.Size - 1) >> Tile.Shift;
        Width = width;
        Height = height;
        Format = format;
        _rowBytes = checked(Width * Format.BytesPerPixel);
        _band = ArrayPool<byte>.Shared.Rent(checked(_rowBytes * Tile.Size));
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int RowsWritten => _nextRow;

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        ObjectDisposedException.ThrowIf(_band == null, this);
        if (y != _nextRow || rowCount < 0 || rowCount > Height - y)
        {
            throw new ImageOperationException($"Decoder delivered rows {y}..{(long)y + rowCount} but {_nextRow} was expected next (frame height {Height}).");
        }

        if ((long)rowCount * _rowBytes > rows.Length)
        {
            throw new ImageOperationException("Decoder delivered fewer bytes than the row count implies.");
        }

        int done = 0;
        while (done < rowCount)
        {
            int n = Math.Min(rowCount - done, Tile.Size - _bandRows);
            rows.Slice(done * _rowBytes, n * _rowBytes).CopyTo(_band.AsSpan(_bandRows * _rowBytes));
            _bandRows += n;
            done += n;
            _nextRow += n;
            if (_bandRows == Tile.Size || _nextRow == Height)
            {
                FlushBand();
            }
        }
    }

    public void Dispose()
    {
        byte[]? b = _band;
        _band = null;
        if (b != null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private void FlushBand()
    {
        int bpp = Format.BytesPerPixel;
        int ty = (_nextRow - _bandRows) >> Tile.Shift;
        int tileStride = Tile.Size * bpp;
        var row = new Tile[_tilesX];
        int made = 0;
        try
        {
            for (int tx = 0; tx < _tilesX; tx++)
            {
                int x0 = tx << Tile.Shift;
                int w = Math.Min(Tile.Size, Width - x0);
                Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
                row[made++] = tile;
                for (int r = 0; r < _bandRows; r++)
                {
                    _band.AsSpan((r * _rowBytes) + (x0 * bpp), w * bpp).CopyTo(tile.Span.Slice(r * tileStride));
                }
            }
        }
        catch
        {
            for (int i = 0; i < made; i++)
            {
                row[i].Release();
            }

            throw;
        }

        _bandRows = 0;

        // The receiver owns the tiles from here on, including when it throws.
        _receiver.Accept(ty, row);
    }

    private sealed class GridReceiver(TileGrid grid) : ITileRowReceiver
    {
        public void Accept(int ty, Tile[] tiles) => tiles.CopyTo(grid.Tiles, ty * grid.TilesX);
    }
}

/// <summary>Takes ownership of a finished row of tiles. When it throws it must already have released them.</summary>
internal interface ITileRowReceiver
{
    void Accept(int ty, Tile[] tiles);
}

/// <summary>Writes the rows of a small region (one tile) straight into a tile buffer.</summary>
internal sealed class TileRowSink : IRowSink
{
    private readonly Tile _tile;
    private readonly int _bpp;
    private int _nextRow;

    public TileRowSink(Tile tile, int width, int height, PixelFormat format)
    {
        _tile = tile;
        Width = width;
        Height = height;
        Format = format;
        _bpp = format.BytesPerPixel;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int RowsWritten => _nextRow;

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        if (y != _nextRow || rowCount < 0 || rowCount > Height - y)
        {
            throw new ImageOperationException("Decoder delivered rows out of order or beyond the requested region.");
        }

        int rowBytes = Width * _bpp;
        if ((long)rowCount * rowBytes > rows.Length)
        {
            throw new ImageOperationException("Decoder delivered fewer bytes than the row count implies.");
        }

        for (int r = 0; r < rowCount; r++)
        {
            rows.Slice(r * rowBytes, rowBytes).CopyTo(_tile.Span.Slice((y + r) * Tile.Size * _bpp));
        }

        _nextRow += rowCount;
    }
}

/// <summary>Discards rows (used to validate that a frame decodes).</summary>
internal sealed class NullRowSink : IRowSink
{
    private int _nextRow;

    public NullRowSink(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int RowsWritten => _nextRow;

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        if (y != _nextRow || rowCount < 0 || rowCount > Height - y)
        {
            throw new ImageOperationException("Decoder delivered rows out of order or beyond the frame.");
        }

        _nextRow += rowCount;
    }
}
