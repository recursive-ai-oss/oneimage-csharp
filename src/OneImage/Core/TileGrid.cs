namespace OneImage;

/// <summary>
/// The tile table of a canvas (or of a fully decoded frame): a sparse array of tiles. A null slot reads as the
/// <see cref="Backing"/> node's pixels when there is one (a canvas made by <c>ToCanvas</c>), else as zeros.
/// Once <see cref="Shared"/> is set (a snapshot references it) the table is never modified again: the canvas clones it first.
/// This is the unit a disk-spilling tile store would replace.
/// A canvas's table owns one reference on every tile it holds, and <see cref="Clone"/> takes its own. The references are given back by
/// <see cref="ReleaseAll"/>, or, for a table retired to snapshots, by a finalizer once <see cref="Arm"/> has installed one, so a tile
/// returns to the pool when the last table holding it is gone. Tables that die unarmed leave their tiles to the garbage collector.
/// </summary>
internal sealed class TileGrid
{
    private TileGridReleaser? _releaser;

    public TileGrid(int width, int height, PixelFormat format, Node? backing)
    {
        ImageLimits.CheckEngine(width, height);
        Width = width;
        Height = height;
        Format = format;
        Backing = backing;
        TilesX = (width + Tile.Size - 1) >> Tile.Shift;
        TilesY = (height + Tile.Size - 1) >> Tile.Shift;
        Tiles = new Tile?[TilesX * TilesY];
    }

    private TileGrid(TileGrid other)
    {
        Width = other.Width;
        Height = other.Height;
        Format = other.Format;
        Backing = other.Backing;
        TilesX = other.TilesX;
        TilesY = other.TilesY;
        Tiles = (Tile?[])other.Tiles.Clone();
        foreach (Tile? t in Tiles)
        {
            t?.Retain();
        }
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public Node? Backing { get; }

    public int TilesX { get; }

    public int TilesY { get; }

    public Tile?[] Tiles { get; }

    public bool Shared { get; set; }

    /// <summary>Returns a tracked copy of the table that holds its own reference on every tile. Not thread-safe against writes to this table.</summary>
    public TileGrid Clone() => new(this);

    /// <summary>Gives back this table's reference on every tile now and empties it. The table must not be shared with a snapshot.</summary>
    public void ReleaseAll() => TileGridReleaser.ReleaseTiles(Tiles);

    /// <summary>Whether <see cref="Arm"/> has been called.</summary>
    public bool Armed => _releaser != null;

    /// <summary>
    /// Hands the table's references to a finalizer, so they are given back when the table becomes unreachable. Called by the writer on
    /// a table that is shared with snapshots and no longer written. Not done at creation: a table that dies young costs the collector
    /// nothing, while keeping its tiles alive for finalization would copy them into an older generation.
    /// </summary>
    public void Arm() => _releaser ??= new TileGridReleaser(Tiles);
}

/// <summary>Reads the pixels of a <see cref="TileGrid"/> as a graph source. Tiles are already stored, so nothing is cached.</summary>
internal sealed class GridSourceNode : Node
{
    private readonly TileGrid _grid;

    public GridSourceNode(TileGrid grid)
        : base(grid.Width, grid.Height, grid.Format, new OpDescriptor("canvas", new OpParameter("width", (long)grid.Width), new OpParameter("height", (long)grid.Height)))
    {
        _grid = grid;
    }

    public override bool Cacheable => false;

    public override bool DirectStorage => _grid.Backing == null;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Tile? t = _grid.Tiles[(ty * _grid.TilesX) + tx];
        if (t != null)
        {
            t.Retain();

            // The table's finalizer releases its tiles; it must not run between reading the slot and taking our reference.
            GC.KeepAlive(_grid);
            return t;
        }

        return _grid.Backing != null ? _grid.Backing.GetTile(tx, ty, context) : Tile.Zero(Format).Retain();
    }
}

/// <summary>Gives back the tile references of a retired <see cref="TileGrid"/> when it is collected.</summary>
internal sealed class TileGridReleaser(Tile?[] tiles)
{
    ~TileGridReleaser() => ReleaseTiles(tiles);

    /// <summary>Releases and clears every slot; calling it again is harmless.</summary>
    public static void ReleaseTiles(Tile?[] tiles)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            Interlocked.Exchange(ref tiles[i], null)?.Release();
        }
    }
}
