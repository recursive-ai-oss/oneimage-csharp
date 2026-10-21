namespace OneImage;

/// <summary>Interleaves single-channel inputs (same size and sample type) into one gray-alpha, RGB or RGBA image.</summary>
internal sealed class CombineChannelsNode : Node
{
    private readonly int _sampleBytes;

    public CombineChannelsNode(Node[] channels)
        : base(
            channels[0].Width,
            channels[0].Height,
            OutputFormat(channels),
            new OpDescriptor("combine_channels", new OpParameter("count", (long)channels.Length)),
            channels)
    {
        _sampleBytes = channels[0].Format.BytesPerSample;
    }

    private static PixelFormat OutputFormat(Node[] c)
    {
        SampleType sample = c[0].Format.Sample;
        return c.Length switch
        {
            2 => new PixelFormat(ColorModel.Gray, sample, AlphaMode.Straight),
            3 => new PixelFormat(ColorModel.Rgb, sample),
            _ => new PixelFormat(ColorModel.Rgb, sample, AlphaMode.Straight),
        };
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        int n = Inputs.Length;
        var sources = new Tile?[n];
        try
        {
            for (int i = 0; i < n; i++)
            {
                sources[i] = Inputs[i].GetTile(tx, ty, context);
            }

            Tile dst = Tile.Rent(Tile.ByteSize(Format), clear: false);
            ImageRect r = TileRect(tx, ty);
            int bps = _sampleBytes;
            for (int y = 0; y < r.Height; y++)
            {
                int rowStart = y * Tile.Size;
                for (int i = 0; i < n; i++)
                {
                    ReadOnlySpan<byte> s = sources[i]!.Span;
                    Span<byte> d = dst.Span;
                    for (int x = 0; x < r.Width; x++)
                    {
                        s.Slice((rowStart + x) * bps, bps).CopyTo(d.Slice((((rowStart + x) * n) + i) * bps, bps));
                    }
                }
            }

            return dst;
        }
        finally
        {
            foreach (Tile? t in sources)
            {
                t?.Release();
            }
        }
    }
}
