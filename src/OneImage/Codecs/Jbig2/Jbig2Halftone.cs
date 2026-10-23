namespace OneImage.Codecs.Jbig2;

/// <summary>The parameters of the halftone region decoding procedure (T.88 6.6.2).</summary>
internal sealed class Jbig2HalftoneParameters
{
    /// <summary>HBW.</summary>
    public int Width { get; init; }

    /// <summary>HBH.</summary>
    public int Height { get; init; }

    /// <summary>HMMR.</summary>
    public bool Mmr { get; init; }

    /// <summary>HTEMPLATE.</summary>
    public int Template { get; init; }

    /// <summary>HENABLESKIP.</summary>
    public bool EnableSkip { get; init; }

    /// <summary>HCOMBOP.</summary>
    public Jbig2CombinationOperator CombinationOperator { get; init; }

    /// <summary>HDEFPIXEL.</summary>
    public int DefaultPixel { get; init; }

    /// <summary>HGW.</summary>
    public int GridWidth { get; init; }

    /// <summary>HGH.</summary>
    public int GridHeight { get; init; }

    /// <summary>HGX, 256 times the grid origin.</summary>
    public int GridX { get; init; }

    /// <summary>HGY.</summary>
    public int GridY { get; init; }

    /// <summary>HRX, 256 times the grid vector.</summary>
    public int VectorX { get; init; }

    /// <summary>HRY.</summary>
    public int VectorY { get; init; }

    /// <summary>HPATS.</summary>
    public required Jbig2Bitmap[] Patterns { get; init; }
}

/// <summary>The pattern dictionary (T.88 6.7) and halftone region (6.6, annex C) decoding procedures.</summary>
internal static class Jbig2Halftone
{
    /// <summary>Decodes a pattern dictionary's data into its patterns.</summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="mmr">HDMMR.</param>
    /// <param name="template">HDTEMPLATE.</param>
    /// <param name="patternWidth">HDPW.</param>
    /// <param name="patternHeight">HDPH.</param>
    /// <param name="grayMax">GRAYMAX; the dictionary holds one more pattern.</param>
    /// <param name="data">The array holding the segment data.</param>
    /// <param name="start">First coded byte.</param>
    /// <param name="end">End of the segment data.</param>
    public static Jbig2Bitmap[] DecodePatternDictionary(Jbig2Work work, bool mmr, int template, int patternWidth, int patternHeight, uint grayMax, byte[] data, int start, int end)
    {
        if (patternWidth < 1 || patternHeight < 1)
        {
            throw new ImageFormatException("A JBIG2 pattern dictionary has a pattern of zero size.");
        }

        if (grayMax >= (uint)work.Jbig2.MaxPatterns)
        {
            throw new ImageLimitException("A JBIG2 pattern dictionary has more patterns than the limit allows.");
        }

        int count = (int)grayMax + 1;
        long totalWidth = (long)count * patternWidth;
        work.CheckRegion(totalWidth, patternHeight);
        work.ChargeRetained((totalWidth + 7) / 8 * patternHeight);
        Jbig2Bitmap collective;
        if (mmr)
        {
            collective = Jbig2Generic.DecodeMmr(work, data, start, end, (int)totalWidth, patternHeight, out _);
        }
        else
        {
            work.CheckDensity(totalWidth, patternHeight, end - start);
            var mq = new Jbig2ArithmeticDecoder(data, start, end);

            // Table 27: the first adaptive pixel is one pattern width to the left, the others sit at their nominal places.
            int[] at = template == 0 ? [-patternWidth, 0, -3, -1, 2, -2, -2, -2] : [-patternWidth, 0];
            collective = Jbig2Generic.DecodeArithmetic(work, mq, new byte[1 << 16], (int)totalWidth, patternHeight, template, false, null, at);
        }

        var patterns = new Jbig2Bitmap[count];
        for (int i = 0; i < count; i++)
        {
            patterns[i] = collective.Extract(i * patternWidth, 0, patternWidth, patternHeight);
        }

        return patterns;
    }

    /// <summary>Decodes a halftone region's data: the gray-scale bit planes and the patterns they select.</summary>
    /// <param name="work">Limits and cancellation.</param>
    /// <param name="p">The parameters.</param>
    /// <param name="data">The array holding the segment data.</param>
    /// <param name="start">First coded byte (after the halftone header).</param>
    /// <param name="end">End of the segment data.</param>
    public static Jbig2Bitmap Decode(Jbig2Work work, Jbig2HalftoneParameters p, byte[] data, int start, int end)
    {
        Jbig2Bitmap[] patterns = p.Patterns;
        if (patterns.Length == 0)
        {
            throw new ImageFormatException("A JBIG2 halftone region refers to an empty pattern dictionary.");
        }

        long cells = (long)p.GridWidth * p.GridHeight;
        if (p.GridWidth < 0 || p.GridHeight < 0 || cells > work.Jbig2.MaxHalftoneCells || p.GridWidth > work.Limits.MaxDimension || p.GridHeight > work.Limits.MaxDimension)
        {
            throw new ImageLimitException("A JBIG2 halftone grid is larger than the limit allows.");
        }

        int pw = patterns[0].Width;
        int ph = patterns[0].Height;
        work.ChargePixels(p.Width, p.Height);
        var region = new Jbig2Bitmap(p.Width, p.Height);
        if (p.DefaultPixel != 0)
        {
            region.Fill(1);
        }

        if (cells == 0)
        {
            return region;
        }

        // 6.6.5.1: grid cells whose pattern would fall entirely outside the region are not coded.
        Jbig2Bitmap? skip = null;
        if (p.EnableSkip)
        {
            work.ChargePixels(p.GridWidth, p.GridHeight);
            skip = new Jbig2Bitmap(p.GridWidth, p.GridHeight);
            for (int mg = 0; mg < p.GridHeight; mg++)
            {
                for (int ng = 0; ng < p.GridWidth; ng++)
                {
                    GridPosition(p, mg, ng, out long x, out long y);
                    if (x + pw <= 0 || x >= p.Width || y + ph <= 0 || y >= p.Height)
                    {
                        skip.Set(ng, mg, 1);
                    }
                }
            }
        }

        int bitsPerValue = Jbig2TextRegion.CeilLog2(patterns.Length);
        var values = new ushort[cells];
        Jbig2ArithmeticDecoder? mq = null;
        byte[]? cx = null;
        if (!p.Mmr)
        {
            work.CheckDensity(p.GridWidth, p.GridHeight, end - start);
            mq = new Jbig2ArithmeticDecoder(data, start, end);
            cx = new byte[1 << 16];
        }

        // Annex C.5: bit planes from the most significant down, each Gray-coded against the plane above it.
        Jbig2Bitmap? previous = null;
        int pos = start;
        for (int j = bitsPerValue - 1; j >= 0; j--)
        {
            Jbig2Bitmap plane = p.Mmr
                ? Jbig2Generic.DecodeMmr(work, data, pos, end, p.GridWidth, p.GridHeight, out pos)
                : Jbig2Generic.DecodeArithmetic(work, mq!, cx!, p.GridWidth, p.GridHeight, p.Template, false, skip, Jbig2Generic.NominalAt(p.Template));
            if (previous is not null)
            {
                for (int i = 0; i < plane.Data.Length; i++)
                {
                    plane.Data[i] ^= previous.Data[i];
                }
            }

            for (int mg = 0; mg < p.GridHeight; mg++)
            {
                int row = mg * p.GridWidth;
                for (int ng = 0; ng < p.GridWidth; ng++)
                {
                    values[row + ng] = (ushort)((values[row + ng] << 1) | plane.Get(ng, mg));
                }
            }

            previous = plane;
        }

        for (int mg = 0; mg < p.GridHeight; mg++)
        {
            work.Cancellation.ThrowIfCancellationRequested();
            for (int ng = 0; ng < p.GridWidth; ng++)
            {
                GridPosition(p, mg, ng, out long x, out long y);
                int v = Math.Min((int)values[(mg * p.GridWidth) + ng], patterns.Length - 1);
                region.Compose(patterns[v], x, y, p.CombinationOperator);
            }
        }

        return region;
    }

    private static void GridPosition(Jbig2HalftoneParameters p, int mg, int ng, out long x, out long y)
    {
        // T.88 6.6.5.1 and 6.6.5.2: arithmetic shifts of the fixed-point grid coordinates.
        x = ((long)p.GridX + ((long)mg * p.VectorY) + ((long)ng * p.VectorX)) >> 8;
        y = ((long)p.GridY + ((long)mg * p.VectorX) - ((long)ng * p.VectorY)) >> 8;
    }
}
