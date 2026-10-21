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
/// A frame decoded on demand. Decoders that report <see cref="DecoderCapabilities.RandomAccess"/> are asked for one tile
/// at a time (cached, recomputed after eviction). Others decode the frame once, on first pull, through the
/// <see cref="StripToTileBuffer"/> into tiles this node owns for its lifetime. The node can be re-created with a
/// region (crop push-down) or a scale (shrink-on-load) when the decoder supports them.
/// </summary>
internal sealed class DecodeSourceNode : Node
{
    private readonly DecodeSession _session;
    private readonly int _frame;
    private readonly int _scale;
    private readonly ImageRect _region;
    private readonly bool _randomAccess;
    private readonly object _gate = new();
    private TileGrid? _grid;

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
    }

    public override bool Cacheable => _randomAccess;

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

    /// <summary>Decodes everything now so errors surface (the fail-up-front mode of load).</summary>
    public void Validate(CancellationToken cancellationToken)
    {
        if (!_randomAccess)
        {
            EnsureGrid(cancellationToken);
            return;
        }

        using Stream s = _session.Source.Open();
        var sink = new NullRowSink(Width, Height, Format);
        _session.Decoder.DecodeFrame(s, _session.Info, _frame, new DecodeRequest(_region, _scale), sink, _session.Limits, cancellationToken);
        CheckComplete(sink.RowsWritten, Height);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        if (!_randomAccess)
        {
            TileGrid grid = EnsureGrid(context.CancellationToken);
            return grid.Tiles[(ty * grid.TilesX) + tx]!.Retain();
        }

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

    private TileGrid EnsureGrid(CancellationToken cancellationToken)
    {
        TileGrid? g = Volatile.Read(ref _grid);
        if (g != null)
        {
            return g;
        }

        lock (_gate)
        {
            if (_grid != null)
            {
                return _grid;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using Stream s = _session.Source.Open();
            var grid = new TileGrid(Width, Height, Format, null);
            using var sink = new StripToTileBuffer(grid);
            DecoderCapabilities caps = _session.Decoder.Capabilities;
            ImageRect request = (caps & DecoderCapabilities.Region) != 0 ? _region : new ImageRect(0, 0, Width, Height);
            _session.Decoder.DecodeFrame(s, _session.Info, _frame, new DecodeRequest(request, _scale), sink, _session.Limits, cancellationToken);
            CheckComplete(sink.RowsWritten, Height);
            Volatile.Write(ref _grid, grid);
            return grid;
        }
    }
}
