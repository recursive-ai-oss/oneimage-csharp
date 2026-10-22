namespace OneImage;

/// <summary>What the frames decoded from one image share: where the bytes are, who decodes them, and the parsed header.</summary>
internal sealed class DecodeSession
{
    public DecodeSession(ImageSource source, IImageDecoder decoder, ImageInfo info, ImageLimits limits)
    {
        Source = source;
        Decoder = decoder;
        Info = info;
        Limits = limits;
    }

    public ImageSource Source { get; }

    public IImageDecoder Decoder { get; }

    public ImageInfo Info { get; }

    public ImageLimits Limits { get; }
}

/// <summary>
/// A frame decoded on demand, in one of three modes picked from the decoder's capabilities and the frame size:
/// <list type="bullet">
/// <item><b>Random access</b> (<see cref="DecoderCapabilities.RandomAccess"/>): the decoder is asked for one tile at a time; tiles are cached and recomputed after eviction.</item>
/// <item><b>Streaming</b> (sequential decoder, frame of at least <see cref="StreamingMinBytes"/>): one decoder pass runs on its own thread in lock step
/// with the consumer and only a sliding window of tile rows is kept (see <see cref="StreamingSession"/>), so memory depends on width, not height.
/// A consumer that needs a row that already left the window starts a new pass from the top (the first <see cref="MaxRestarts"/> times);
/// after that the frame is decoded once into tiles this node keeps for its lifetime, like the next mode.</item>
/// <item><b>Materialized</b> (sequential decoder, small frame): decoded once into tiles kept for the node's lifetime, through the <see cref="StripToTileBuffer"/>.</item>
/// </list>
/// The node can be re-created with a region (crop push-down) or a scale (shrink-on-load) when the decoder supports them.
/// </summary>
internal sealed class DecodeSourceNode : Node
{
    /// <summary>Frames of at least this many bytes stream instead of materializing. Settable so tests can exercise streaming on small images.</summary>
    public static long StreamingMinBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>How many extra passes a streaming node starts for consumers that go back before it materializes the frame.</summary>
    public const int MaxRestarts = 2;

    private readonly DecodeSession _session;
    private readonly int _frame;
    private readonly int _scale;
    private readonly ImageRect _region;
    private readonly bool _randomAccess;
    private readonly bool _streaming;
    private readonly object _gate = new();
    private TileGrid? _grid;
    private StreamingSessionHandle? _stream;
    private int _restarts;

    public DecodeSourceNode(DecodeSession session, int frame, int scale = 1, ImageRect? region = null)
        : this(session, frame, scale, region ?? FullRegion(session, frame, scale), 0)
    {
    }

    private DecodeSourceNode(DecodeSession session, int frame, int scale, ImageRect region, int unused)
        : base(
            region.Width,
            region.Height,
            session.Info.Frames[frame].Format,
            new OpDescriptor(
                "decode",
                new OpParameter("format", session.Info.Format.Name),
                new OpParameter("frame", (long)frame),
                new OpParameter("scale", (long)scale),
                new OpParameter("x", (long)region.X),
                new OpParameter("y", (long)region.Y),
                new OpParameter("width", (long)region.Width),
                new OpParameter("height", (long)region.Height)))
    {
        _ = unused;
        _session = session;
        _frame = frame;
        _scale = scale;
        _region = region;
        DecoderCapabilities caps = session.Decoder.Capabilities;
        _randomAccess = (caps & DecoderCapabilities.RandomAccess) == DecoderCapabilities.RandomAccess;
        _streaming = !_randomAccess && (long)region.Width * region.Height * Format.BytesPerPixel >= StreamingMinBytes;
    }

    public override bool Cacheable => _randomAccess;

    public override bool Streaming => _streaming;

    public override bool HasBlockedSource => _randomAccess && _scale == 1 && _session.Decoder is IBlockedDecoder;

    public override void Prefetch(ImageRect region, RenderContext context)
    {
        if (!HasBlockedSource || context.Degree <= 1 || !((IBlockedDecoder)_session.Decoder).TryGetBlockSize(_session.Info, _frame, out int bw, out int bh) || bw <= 0 || bh <= 0)
        {
            return;
        }

        var decoder = (IBlockedDecoder)_session.Decoder;
        ImageRect full = region.Offset(_region.X, _region.Y);
        int cx0 = full.X / bw;
        int cx1 = (full.Right - 1) / bw;
        int cy0 = full.Y / bh;
        int cy1 = (full.Bottom - 1) / bh;
        int across = cx1 - cx0 + 1;
        int cells = across * (cy1 - cy0 + 1);
        if (cells <= 1)
        {
            return;
        }

        // Cells are whole native blocks clipped to the region; every block is decoded by exactly one worker.
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Degree, CancellationToken = context.CancellationToken };
        try
        {
            Parallel.For(0, cells, options, i =>
            {
                var cell = new ImageRect((cx0 + (i % across)) * bw, (cy0 + (i / across)) * bh, bw, bh).Intersect(full);
                using Stream s = _session.Source.Open();
                decoder.PrefetchBlocks(s, _session.Info, _frame, cell, _session.Limits, context.CancellationToken);
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }

    private bool SupportsRegion => (_session.Decoder.Capabilities & DecoderCapabilities.Region) != 0;

    private bool SupportsScale => (_session.Decoder.Capabilities & DecoderCapabilities.Scale) != 0;

    private bool IsFull => _region == FullRegion(_session, _frame, _scale);

    /// <summary>Crop push-down: a node producing just <paramref name="rect"/> (this node's coordinates), or null if the decoder cannot or the frame is already decoded.</summary>
    public DecodeSourceNode? TryPushRegion(ImageRect rect)
    {
        if (!SupportsRegion || Volatile.Read(ref _grid) != null)
        {
            return null;
        }

        return new DecodeSourceNode(_session, _frame, _scale, rect.Offset(_region.X, _region.Y), 0);
    }

    /// <summary>Shrink-on-load: a node producing the frame at 1/<paramref name="denominator"/> size, or null if unsupported.</summary>
    public DecodeSourceNode? TryPushScale(int denominator)
    {
        int combined = _scale * denominator;
        if (!SupportsScale || !IsFull || Volatile.Read(ref _grid) != null || denominator is not (2 or 4 or 8) || combined > 8)
        {
            return null;
        }

        return new DecodeSourceNode(_session, _frame, combined);
    }

    /// <summary>Decodes everything now so errors surface (the fail-up-front mode of load). Small sequential frames keep the result; others decode once and discard it.</summary>
    public void Validate(CancellationToken cancellationToken)
    {
        if (!_randomAccess && !_streaming)
        {
            EnsureGrid(cancellationToken);
            return;
        }

        using Stream s = _session.Source.Open();
        var sink = new NullRowSink(Width, Height, Format);
        _session.Decoder.DecodeFrame(s, _session.Info, _frame, new DecodeRequest(RequestRegion(), _scale), sink, _session.Limits, cancellationToken);
        CheckComplete(sink.RowsWritten, Height);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        if (_randomAccess)
        {
            return DecodeOneTile(tx, ty, context);
        }

        if (_streaming)
        {
            return StreamTile(tx, ty, context.CancellationToken);
        }

        return GridTile(tx, ty, context.CancellationToken);
    }

    /// <summary>A shrunk or cropped decode exists for one pipeline; a full-size decode may be shared by several.</summary>
    protected override bool PipelinePrivate => _scale > 1 || !IsFull;

    protected override void ReleaseMaterialized()
    {
        TileGrid? grid;
        lock (_gate)
        {
            grid = _grid;
            _grid = null;
        }

        if (grid == null)
        {
            return;
        }

        // Readers that already hold a tile keep it; new readers decode again.
        foreach (Tile? t in grid.Tiles)
        {
            t?.Release();
        }
    }

    private Tile GridTile(int tx, int ty, CancellationToken cancellationToken)
    {
        while (true)
        {
            TileGrid grid = EnsureGrid(cancellationToken);
            if (grid.Tiles[(ty * grid.TilesX) + tx]!.TryRetain())
            {
                return grid.Tiles[(ty * grid.TilesX) + tx]!;
            }
        }
    }

    private static ImageRect FullRegion(DecodeSession session, int frame, int scale)
    {
        FrameInfo f = session.Info.Frames[frame];
        return new ImageRect(0, 0, (f.Width + scale - 1) / scale, (f.Height + scale - 1) / scale);
    }

    private static void CheckComplete(int delivered, int expected)
    {
        if (delivered != expected)
        {
            throw new ImageFormatException($"Image data ended after {delivered} of {expected} rows.");
        }
    }

    /// <summary>The region handed to a sequential decoder: only decoders that honor regions get anything but the whole frame.</summary>
    private ImageRect RequestRegion() => SupportsRegion ? _region : new ImageRect(0, 0, Width, Height);

    private Tile DecodeOneTile(int tx, int ty, RenderContext context)
    {
        ImageRect tr = TileRect(tx, ty);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            using Stream s = _session.Source.Open();
            var sink = new TileRowSink(tile, tr.Width, tr.Height, Format);
            _session.Decoder.DecodeFrame(s, _session.Info, _frame, new DecodeRequest(tr.Offset(_region.X, _region.Y), _scale), sink, _session.Limits, context.CancellationToken);
            CheckComplete(sink.RowsWritten, tr.Height);
            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
    }

    private Tile StreamTile(int tx, int ty, CancellationToken cancellationToken)
    {
        while (true)
        {
            TileGrid? grid = Volatile.Read(ref _grid);
            if (grid != null)
            {
                return GridTile(tx, ty, cancellationToken);
            }

            StreamingSessionHandle? handle = CurrentSession(ty);
            if (handle == null)
            {
                continue;
            }

            Tile? tile = handle.Session.TryGetTile(tx, ty, cancellationToken);
            if (tile != null)
            {
                return tile;
            }

            Recover(handle, cancellationToken);
        }
    }

    private StreamingSessionHandle? CurrentSession(int startRow)
    {
        StreamingSessionHandle? h = Volatile.Read(ref _stream);
        if (h != null)
        {
            return h;
        }

        lock (_gate)
        {
            // Null once the frame has been materialized: the caller then reads the grid.
            return _grid != null ? null : _stream ??= StartPass(startRow);
        }
    }

    private StreamingSessionHandle StartPass(int startRow) =>
        new(new StreamingSession(_session, _frame, _scale, RequestRegion(), Width, Height, Format, startRow));

    /// <summary>A consumer needed a row that left the window: decode again from the top, or keep the whole frame once restarts run out.</summary>
    private void Recover(StreamingSessionHandle failed, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_stream, failed))
            {
                return;
            }

            failed.Dispose();
            _stream = null;
            if (++_restarts > MaxRestarts)
            {
                MaterializeLocked(cancellationToken);
            }
        }
    }

    private TileGrid EnsureGrid(CancellationToken cancellationToken)
    {
        TileGrid? g = Volatile.Read(ref _grid);
        if (g != null)
        {
            return g;
        }

        lock (_gate)
        {
            return MaterializeLocked(cancellationToken);
        }
    }

    private TileGrid MaterializeLocked(CancellationToken cancellationToken)
    {
        if (_grid != null)
        {
            return _grid;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using Stream s = _session.Source.Open();
        var grid = new TileGrid(Width, Height, Format, null);
        using var sink = new StripToTileBuffer(grid);
        _session.Decoder.DecodeFrame(s, _session.Info, _frame, new DecodeRequest(RequestRegion(), _scale), sink, _session.Limits, cancellationToken);
        CheckComplete(sink.RowsWritten, Height);
        _stream?.Dispose();
        _stream = null;
        Volatile.Write(ref _grid, grid);
        return grid;
    }
}
