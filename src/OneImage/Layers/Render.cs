namespace OneImage.Layers;

/// <summary>
/// An immutable, frozen state of a <see cref="Document"/> (see <see cref="Document.Snapshot"/>). It can be rendered from any thread
/// while the document keeps changing; renders of snapshots of one document share its cache of composited tiles, and a tile is
/// reused whenever no edit between the two snapshots touched it.
/// </summary>
public sealed class DocumentSnapshot
{
    internal DocumentSnapshot(Document document, LayerState[] layers, long revision, RenderCache cache, Tile background, bool linear, ImageMetadata metadata, SpillTileStore? store)
    {
        Width = document.Width;
        Height = document.Height;
        Format = document.Format;
        Revision = revision;
        Layers = layers;
        Cache = cache;
        Background = background;
        Linear = linear;
        Metadata = metadata;
        Store = store;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixel format of the rendered image.</summary>
    public PixelFormat Format { get; }

    /// <summary>Gets a number that grows with every snapshot that saw a change. Equal revisions mean identical pixels.</summary>
    public long Revision { get; }

    internal LayerState[] Layers { get; }

    internal RenderCache Cache { get; }

    internal Tile Background { get; }

    internal bool Linear { get; }

    internal ImageMetadata Metadata { get; }

    internal SpillTileStore? Store { get; }

    /// <summary>
    /// Returns the composited document as a lazy <see cref="Image"/> of the document's size and format (and its color profile). Nothing is
    /// computed until pixels are pulled, and only the tiles a region needs are. Pulling after the document was disposed fails when a spill store was used.
    /// </summary>
    /// <returns>The image.</returns>
    public Image Render() => Image.FromNode(new DocumentNode(this)).WithMetadata(Metadata);
}

/// <summary>The frozen form of one layer, as the renderer sees it.</summary>
internal sealed class LayerState(
    LayerKind kind,
    bool visible,
    float opacity,
    BlendMode blend,
    bool clip,
    ImageRect bounds,
    Node? node,
    TileGrid? grid,
    MaskState? mask,
    bool passThrough,
    LayerState[] children,
    Adjustment? adjustment)
{
    public LayerKind Kind { get; } = kind;

    public bool Visible { get; } = visible;

    /// <summary>Opacity times fill opacity.</summary>
    public float Opacity { get; } = opacity;

    public BlendMode Blend { get; } = blend;

    public bool Clip { get; } = clip;

    public ImageRect Bounds { get; } = bounds;

    /// <summary>The pixels (a pixel layer's tile table or a fill layer's filled surface), placed at the bounds' top-left corner.</summary>
    public Node? Node { get; } = node;

    public TileGrid? Grid { get; } = grid;

    public MaskState? Mask { get; } = mask;

    public bool PassThrough { get; } = passThrough;

    public LayerState[] Children { get; } = children;

    public Adjustment? Adjustment { get; } = adjustment;
}

/// <summary>The graph node behind <see cref="DocumentSnapshot.Render"/>: computes a tile by walking the layers, through the document's tile cache.</summary>
internal sealed class DocumentNode : Node
{
    private readonly DocumentSnapshot _snapshot;

    public DocumentNode(DocumentSnapshot snapshot)
        : base(
            snapshot.Width,
            snapshot.Height,
            snapshot.Format,
            new OpDescriptor("document", new OpParameter("revision", snapshot.Revision), new OpParameter("layers", (long)snapshot.Layers.Length)))
    {
        _snapshot = snapshot;
    }

    // The document keeps its own cache (and invalidates it by edit region), so the shared one stays out of it.
    public override bool Cacheable => false;

    protected override bool PipelinePrivate => false;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        int index = (ty * TilesX) + tx;
        RenderCache cache = _snapshot.Cache;
        Tile? hit = cache.TryGet(index, _snapshot.Revision);
        if (hit != null)
        {
            return hit;
        }

        Tile tile = new TileJob(_snapshot, TileRect(tx, ty), context.Nested).Run();
        cache.CountComputed();
        cache.Put(index, tile, _snapshot.Revision, _snapshot.Background);
        return tile;
    }
}

/// <summary>
/// The document's cache of composited tiles. An entry knows the revision it was computed at; the cache knows the revision at which each
/// tile last changed. A snapshot at revision <c>r</c> may use an entry computed at <c>c</c> when the tile has not changed after <c>min(c, r)</c>.
/// </summary>
internal sealed class RenderCache
{
    private readonly object _gate = new();
    private readonly Tile?[] _tiles;
    private readonly long[] _computedAt;
    private readonly long[] _changedAt;
    private readonly int[] _cost;
    private readonly Queue<int> _order = new();
    private readonly long _budget;
    private readonly SpillTileStore? _store;
    private long _bytes;
    private long _computed;
    private long _reused;

    public RenderCache(int tileCount, long budget, SpillTileStore? store)
    {
        _tiles = new Tile?[tileCount];
        _computedAt = new long[tileCount];
        _changedAt = new long[tileCount];
        _cost = new int[tileCount];
        _budget = budget;
        _store = store;
    }

    public long Computed => Interlocked.Read(ref _computed);

    public long Reused => Interlocked.Read(ref _reused);

    public void CountComputed() => Interlocked.Increment(ref _computed);

    public Tile? TryGet(int index, long snapshotRevision)
    {
        lock (_gate)
        {
            Tile? t = _tiles[index];
            if (t != null && _changedAt[index] <= Math.Min(_computedAt[index], snapshotRevision) && t.TryRetain())
            {
                Interlocked.Increment(ref _reused);
                return t;
            }
        }

        return null;
    }

    public void Put(int index, Tile tile, long snapshotRevision, Tile shared)
    {
        Tile? keep = null;
        bool isShared = ReferenceEquals(tile, shared);
        if (_store != null && !isShared)
        {
            // With a spill store the cached copy lives in it, sealed, so it can go to disk instead of counting against memory.
            keep = Tile.Rent(tile.Length, clear: false, _store);
            tile.Span.CopyTo(keep.Span);
            keep.Seal();
        }

        lock (_gate)
        {
            if (_changedAt[index] > snapshotRevision || (_tiles[index] != null && _computedAt[index] > snapshotRevision && _changedAt[index] <= _computedAt[index]))
            {
                keep?.Release();
                return;
            }

            Tile stored = keep ?? tile.Retain();
            Tile? old = _tiles[index];
            _tiles[index] = stored;
            _computedAt[index] = snapshotRevision;
            if (old != null)
            {
                _bytes -= _cost[index];
                old.Release();
            }

            _cost[index] = isShared ? 0 : stored.Length;
            _bytes += _cost[index];
            _order.Enqueue(index);
            if (_store == null)
            {
                while (_bytes > _budget && _order.TryDequeue(out int victim))
                {
                    Tile? v = _tiles[victim];
                    if (v != null && victim != index)
                    {
                        _tiles[victim] = null;
                        _bytes -= _cost[victim];
                        v.Release();
                    }
                }
            }
            else if (_order.Count > 4 * _tiles.Length)
            {
                _order.Clear();
            }
        }
    }

    /// <summary>Records that the tiles flagged in <paramref name="changed"/> differ from every earlier revision.</summary>
    public void MarkChanged(bool[] changed, long revision)
    {
        lock (_gate)
        {
            for (int i = 0; i < changed.Length; i++)
            {
                if (changed[i])
                {
                    _changedAt[i] = revision;
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            for (int i = 0; i < _tiles.Length; i++)
            {
                _tiles[i]?.Release();
                _tiles[i] = null;
            }

            _bytes = 0;
            _order.Clear();
        }
    }
}
