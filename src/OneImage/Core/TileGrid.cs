namespace OneImage;

/// <summary>
/// The tile table of a canvas (or of a fully decoded frame): a sparse array of tiles. A null slot reads as the
/// <see cref="Backing"/> node's pixels when there is one (a canvas made by <c>ToCanvas</c>), else as zeros.
/// Once <see cref="Shared"/> is set (a snapshot references it) the table is never modified again: the canvas clones it first.
/// This is the unit a disk-spilling tile store would replace.
/// </summary>
internal sealed class TileGrid
{
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
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public Node? Backing { get; }

    public int TilesX { get; }

    public int TilesY { get; }

    public Tile?[] Tiles { get; }

    public bool Shared { get; set; }

    public TileGrid Clone() => new(this);
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
            return t.Retain();
        }

        return _grid.Backing != null ? _grid.Backing.GetTile(tx, ty, context) : Tile.Zero(Format).Retain();
    }
}
