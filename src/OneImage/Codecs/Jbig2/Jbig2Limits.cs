namespace OneImage.Codecs.Jbig2;

/// <summary>
/// Bounds on what the JBIG2 decoder accepts, checked against header values before the allocation they guard.
/// The page size itself is bounded by <see cref="ImageLimits"/>; these cover the parts of a JBIG2 stream that
/// can ask for more work than their size suggests: symbol counts, instance counts, Huffman tables, regions that
/// declare far more pixels than they carry data for, and the sum of everything decoded.
/// </summary>
internal sealed class Jbig2Limits
{
    /// <summary>Gets the default limits.</summary>
    public static Jbig2Limits Default { get; } = new();

    /// <summary>Gets the most segments one stream (globals plus page data) may contain.</summary>
    public int MaxSegments { get; init; } = 1 << 17;

    /// <summary>Gets the most segments one segment may refer to.</summary>
    public int MaxReferredSegments { get; init; } = 1 << 16;

    /// <summary>Gets the most symbols one dictionary may define or export.</summary>
    public int MaxSymbolsPerDictionary { get; init; } = 1 << 20;

    /// <summary>Gets the most pixels in one symbol or pattern bitmap.</summary>
    public long MaxSymbolPixels { get; init; } = 1L << 24;

    /// <summary>Gets the most bytes of symbol and pattern bitmaps the decoder keeps for later segments, summed over dictionaries.</summary>
    public long MaxRetainedBytes { get; init; } = 1L << 27;

    /// <summary>Gets the most symbol instances (text region symbols, aggregated symbols) drawn in one stream.</summary>
    public long MaxInstances { get; init; } = 1L << 25;

    /// <summary>Gets the most pixels the decoder allocates and decodes in total, over all regions, symbols and planes.</summary>
    public long MaxDecodedPixels { get; init; } = 1L << 29;

    /// <summary>Gets the most lines of a custom Huffman table.</summary>
    public int MaxHuffmanTableLines { get; init; } = 1 << 16;

    /// <summary>
    /// Gets the most pixels a generic region or bit plane may declare per byte of coded data. A real page compresses
    /// far less than this; a header that declares a huge blank region over a few bytes is refused before allocating.
    /// </summary>
    public long MaxPixelsPerCodedByte { get; init; } = 1L << 20;

    /// <summary>Gets the most patterns a pattern dictionary may define.</summary>
    public int MaxPatterns { get; init; } = 1 << 16;

    /// <summary>Gets the most cells in a halftone grid.</summary>
    public long MaxHalftoneCells { get; init; } = 1L << 24;
}

/// <summary>The running totals and settings of one decode: limits, cancellation and the work counters.</summary>
internal sealed class Jbig2Work
{
    private long _pixels;
    private long _instances;
    private long _retained;

    public Jbig2Work(ImageLimits limits, Jbig2Limits jbig2, CancellationToken cancellationToken)
    {
        Limits = limits;
        Jbig2 = jbig2;
        Cancellation = cancellationToken;
    }

    /// <summary>Gets the image limits (page size).</summary>
    public ImageLimits Limits { get; }

    /// <summary>Gets the JBIG2-specific limits.</summary>
    public Jbig2Limits Jbig2 { get; }

    /// <summary>Gets the cancellation token.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Counts pixels about to be allocated and decoded against the total budget.</summary>
    /// <exception cref="ImageLimitException">The budget is spent.</exception>
    public void ChargePixels(long width, long height)
    {
        long n = width * height;
        _pixels += n;
        if (_pixels > Jbig2.MaxDecodedPixels || _pixels < 0)
        {
            throw new ImageLimitException($"The JBIG2 data asks for more than {Jbig2.MaxDecodedPixels} decoded pixels in total.");
        }
    }

    /// <summary>Counts symbol instances against the budget.</summary>
    public void ChargeInstances(long count)
    {
        _instances += count;
        if (_instances > Jbig2.MaxInstances || _instances < 0)
        {
            throw new ImageLimitException($"The JBIG2 data asks for more than {Jbig2.MaxInstances} symbol instances.");
        }
    }

    /// <summary>Counts bytes of bitmaps kept for later segments against the budget.</summary>
    public void ChargeRetained(long bytes)
    {
        _retained += bytes;
        if (_retained > Jbig2.MaxRetainedBytes || _retained < 0)
        {
            throw new ImageLimitException($"The JBIG2 dictionaries hold more than {Jbig2.MaxRetainedBytes} bytes of bitmaps.");
        }
    }

    /// <summary>Checks the size of a symbol or pattern bitmap before it is allocated.</summary>
    public void CheckSymbol(long width, long height)
    {
        if (width < 0 || height < 0)
        {
            throw new ImageFormatException("A JBIG2 symbol has a negative size.");
        }

        if (width > Limits.MaxDimension || height > Limits.MaxDimension || width * height > Jbig2.MaxSymbolPixels)
        {
            throw new ImageLimitException($"A JBIG2 symbol of {width}x{height} pixels exceeds the limits.");
        }
    }

    /// <summary>
    /// Checks the size of a region against the dimension limits before it is allocated (zero sizes are allowed, since
    /// a region may be empty).
    /// </summary>
    public void CheckRegion(long width, long height)
    {
        if (width < 0 || height < 0)
        {
            throw new ImageFormatException("A JBIG2 region has a negative size.");
        }

        if (width == 0 || height == 0)
        {
            return;
        }

        Limits.CheckFrame(width, height);
    }

    /// <summary>Checks a region of coded data against the pixels-per-byte bound.</summary>
    public void CheckDensity(long width, long height, long codedBytes)
    {
        if (width * height > Jbig2.MaxPixelsPerCodedByte * Math.Max(codedBytes, 1))
        {
            throw new ImageLimitException("A JBIG2 region declares far more pixels than its coded data can describe.");
        }
    }
}
