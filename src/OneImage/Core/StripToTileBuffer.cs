using OneImage.Kernels;
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

/// <summary>A pixelwise op that can run on decoded rows as they arrive (a color conversion).</summary>
internal interface IRowStage
{
    /// <summary>Gets the format the stage produces.</summary>
    PixelFormat OutputFormat { get; }

    /// <summary>Converts one row; safe to call from several threads at once.</summary>
    void ConvertRow(ReadOnlySpan<byte> source, Span<byte> destination, int width);
}

/// <summary>
/// Writes decoded rows into a caller's buffer, converting to its format; the buffer holds exactly the frame, tightly packed.
/// An optional <see cref="IRowStage"/> runs first. Deliveries big enough to be worth it are handed to the thread pool and
/// converted by up to <c>degree</c> threads while the decoder works on the next rows (one delivery in flight, so the memory
/// held is two deliveries). Callers must call <see cref="Drain"/> or <see cref="Abandon"/> before the destination goes away.
/// </summary>
internal sealed class DirectRowSink : IRowSink
{
    /// <summary>Fewer pixels than this per delivery are converted on the decoder's thread: handing them off would cost more than it saves.</summary>
    private const int ParallelMinPixels = 1 << 15;

    private readonly PinnedDestination _destination;
    private readonly PixelFormat _destinationFormat;
    private readonly ConvertRowKernel? _converter;
    private readonly IRowStage? _stage;
    private readonly int _degree;
    private readonly int _sourceRowBytes;
    private readonly int _stageRowBytes;
    private readonly int _destinationRowBytes;
    private readonly byte[]?[] _chunks = new byte[2][];
    private int _nextChunk;
    private Task? _pending;
    private int _nextRow;

    public DirectRowSink(PinnedDestination destination, int width, int height, PixelFormat format, PixelFormat destinationFormat, ConvertRowKernel? converter, IRowStage? stage = null, int degree = 1)
    {
        _destination = destination;
        _destinationFormat = destinationFormat;
        _converter = converter;
        _stage = stage;
        _degree = degree;
        Width = width;
        Height = height;
        Format = format;
        _sourceRowBytes = width * format.BytesPerPixel;
        _stageRowBytes = stage == null ? 0 : width * stage.OutputFormat.BytesPerPixel;
        _destinationRowBytes = width * destinationFormat.BytesPerPixel;
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

        if ((long)rowCount * _sourceRowBytes > rows.Length)
        {
            throw new ImageOperationException("Decoder delivered fewer bytes than the row count implies.");
        }

        if (_stage == null)
        {
            for (int r = 0; r < rowCount; r++)
            {
                WriteRow(rows.Slice(r * _sourceRowBytes, _sourceRowBytes), Format, y + r);
            }
        }
        else if (_degree > 1 && rowCount > 1 && rowCount * (long)Width >= ParallelMinPixels)
        {
            HandOff(y, rows, rowCount);
        }
        else
        {
            Drain();
            byte[]? scratch = _converter == null ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(_stageRowBytes);
            try
            {
                for (int r = 0; r < rowCount; r++)
                {
                    ConvertOne(rows.Slice(r * _sourceRowBytes, _sourceRowBytes), y + r, scratch);
                }
            }
            finally
            {
                if (scratch != null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
                }
            }
        }

        _nextRow = y + rowCount;
    }

    /// <summary>Waits for the delivery in flight, rethrowing its failure.</summary>
    public void Drain()
    {
        Task? t = _pending;
        _pending = null;
        if (t == null)
        {
            return;
        }

        try
        {
            t.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>Waits for the delivery in flight and gives back the buffers, ignoring its failure (the caller is already failing).</summary>
    public void Abandon()
    {
        try
        {
            Drain();
        }
        catch (Exception)
        {
            // The decode already failed for another reason; that one is reported.
        }

        ReleaseChunks();
    }

    /// <summary>Waits for the last delivery and gives back the buffers.</summary>
    public void Finish()
    {
        try
        {
            Drain();
        }
        finally
        {
            ReleaseChunks();
        }
    }

    private void ReleaseChunks()
    {
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (_chunks[i] is { } c)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(c);
                _chunks[i] = null;
            }
        }
    }

    private void HandOff(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        // The decoder reuses its row buffer as soon as this returns, so the delivery is copied; the copy of the previous
        // delivery must be finished with before its buffer is reused (which Drain guarantees).
        Drain();
        int bytes = rowCount * _sourceRowBytes;
        byte[] chunk = _chunks[_nextChunk] ??= System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
        if (chunk.Length < bytes)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
            chunk = _chunks[_nextChunk] = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
        }

        rows.Slice(0, bytes).CopyTo(chunk);
        _nextChunk ^= 1;
        var options = new ParallelOptions { MaxDegreeOfParallelism = _degree };
        _pending = Task.Run(() => Parallel.For(0, rowCount, options, r =>
        {
            // A format change after the stage goes through a scratch row, rented per row so the workers share nothing.
            byte[]? scratch = _converter == null ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(_stageRowBytes);
            try
            {
                ConvertOne(chunk.AsSpan(r * _sourceRowBytes, _sourceRowBytes), y + r, scratch);
            }
            finally
            {
                if (scratch != null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
                }
            }
        }));
    }

    /// <summary>Delivers one row in <paramref name="format"/> to its place in the destination, converting if the destination differs.</summary>
    private void WriteRow(ReadOnlySpan<byte> src, PixelFormat format, int row)
    {
        Span<byte> dst = _destination.Slice(row * _destinationRowBytes, _destinationRowBytes);
        if (_converter == null)
        {
            src.CopyTo(dst);
        }
        else
        {
            _converter(src, format, dst, _destinationFormat, Width);
        }
    }

    /// <summary>Runs the stage on one row and puts the result in the destination (through the scratch row when the formats differ).</summary>
    private void ConvertOne(ReadOnlySpan<byte> src, int row, byte[]? scratch)
    {
        if (scratch == null)
        {
            _stage!.ConvertRow(src, _destination.Slice(row * _destinationRowBytes, _destinationRowBytes), Width);
            return;
        }

        Span<byte> mid = scratch.AsSpan(0, _stageRowBytes);
        _stage!.ConvertRow(src, mid, Width);
        WriteRow(mid, _stage.OutputFormat, row);
    }
}
