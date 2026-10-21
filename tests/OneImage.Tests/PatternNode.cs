namespace OneImage.Tests;

/// <summary>Test source: a pattern computed from the coordinates, counting tile computations and concurrency.</summary>
internal sealed class PatternNode : Node
{
    private int _computed;
    private int _active;
    private int _maxActive;
    private readonly int _delayMs;
    private readonly bool _cacheable;

    public PatternNode(int width, int height, int delayMs = 0, bool cacheable = true)
        : base(width, height, PixelFormat.Rgba8, new OpDescriptor("pattern", new OpParameter("width", (long)width)))
    {
        _delayMs = delayMs;
        _cacheable = cacheable;
    }

    public override bool Cacheable => _cacheable;

    public int Computed => Volatile.Read(ref _computed);

    public int MaxActive => Volatile.Read(ref _maxActive);

    public static Rgba32 Expected(int x, int y) => new((byte)x, (byte)y, (byte)((x >> 8) ^ (y >> 8) ^ (x * 7)), (byte)(255 - ((x + y) & 63)));

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Interlocked.Increment(ref _computed);
        int active = Interlocked.Increment(ref _active);
        int seen;
        while (active > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen)
        {
        }

        try
        {
            if (_delayMs > 0)
            {
                Thread.Sleep(_delayMs);
            }

            Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: true);
            ImageRect r = TileRect(tx, ty);
            for (int y = 0; y < r.Height; y++)
            {
                for (int x = 0; x < r.Width; x++)
                {
                    Rgba32 p = Expected(r.X + x, r.Y + y);
                    int o = ((y * Tile.Size) + x) * 4;
                    tile.Span[o] = p.R;
                    tile.Span[o + 1] = p.G;
                    tile.Span[o + 2] = p.B;
                    tile.Span[o + 3] = p.A;
                }
            }

            return tile;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
