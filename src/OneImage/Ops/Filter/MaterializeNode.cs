namespace OneImage;

/// <summary>
/// Holds its input as stored tiles, computed once, in tile order, on first use. A wide blur is approximated on a reduced copy of
/// the image; a streaming source can only be read forward, but the blur reads its input with a margin and in any order, so the
/// reduced copy is read once, top to bottom, into tiles that stay for the life of the node. The copy is small by construction
/// (the image divided by the square of the reduction factor), so streaming memory stays bounded by a fraction of the frame.
/// </summary>
internal sealed class MaterializeNode : Node
{
    private readonly object _gate = new();
    private Tile?[]? _tiles;

    public MaterializeNode(Node input)
        : base(input.Width, input.Height, input.Format, new OpDescriptor("materialize"), input)
    {
    }

    public override bool Cacheable => false;

    // Still reported as streaming: results derived from it belong to a streaming render and use its small cache,
    // although this node itself can be read in any order.
    public override bool Streaming => true;

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Tile?[] tiles;
        lock (_gate)
        {
            tiles = _tiles ??= Fill(context);
        }

        return tiles[(ty * TilesX) + tx]!.Retain();
    }

    private Tile?[] Fill(RenderContext context)
    {
        var tiles = new Tile?[TilesX * TilesY];
        try
        {
            for (int ty = 0; ty < TilesY; ty++)
            {
                for (int tx = 0; tx < TilesX; tx++)
                {
                    tiles[(ty * TilesX) + tx] = Inputs[0].GetTile(tx, ty, context);
                }
            }

            return tiles;
        }
        catch
        {
            foreach (Tile? t in tiles)
            {
                t?.Release();
            }

            throw;
        }
    }
}
