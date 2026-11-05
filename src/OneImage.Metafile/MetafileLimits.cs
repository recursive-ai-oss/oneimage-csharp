namespace OneImage.Metafile;

/// <summary>
/// Bounds on what a metafile may ask for. Each is checked against the file's own numbers before the memory or time it guards is spent;
/// reaching one throws <see cref="ImageLimitException"/> (or, for per-record sizes, turns the record into a diagnostic).
/// </summary>
public sealed class MetafileLimits
{
    /// <summary>Gets the defaults, suitable for files from untrusted sources.</summary>
    public static MetafileLimits Default { get; } = new();

    /// <summary>Gets the most bytes read from the file (default 256 MiB).</summary>
    public int MaxFileBytes { get; init; } = 256 << 20;

    /// <summary>Gets the most records interpreted (default 4,000,000).</summary>
    public int MaxRecords { get; init; } = 4_000_000;

    /// <summary>Gets the most commands produced (default 8,000,000).</summary>
    public int MaxCommands { get; init; } = 8_000_000;

    /// <summary>
    /// Gets the most bytes of an enhanced metafile embedded in a Windows metafile (default 64 MiB). The declared size is checked before the
    /// buffer is allocated.
    /// </summary>
    public int MaxEmbeddedEmfBytes { get; init; } = 64 << 20;

    /// <summary>Gets the most graphics objects alive at once, and so the largest object index (default 65,536).</summary>
    public int MaxObjects { get; init; } = 1 << 16;

    /// <summary>Gets the most points in one path or polyline, including path brackets (default 4,194,304).</summary>
    public int MaxPathPoints { get; init; } = 1 << 22;

    /// <summary>Gets the most pixels of one bitmap (default 2^26, 64 megapixels).</summary>
    public long MaxBitmapPixels { get; init; } = 1L << 26;

    /// <summary>Gets the most bitmap pixels decoded over the whole file (default 2^28).</summary>
    public long MaxTotalBitmapPixels { get; init; } = 1L << 28;

    /// <summary>Gets the deepest nesting of saved device-context states (default 1,024).</summary>
    public int MaxSaveDepth { get; init; } = 1024;

    /// <summary>Gets the most rectangles of one region (default 1,048,576).</summary>
    public int MaxRegionRectangles { get; init; } = 1 << 20;

    /// <summary>Gets the most characters of one text record (default 1,048,576).</summary>
    public int MaxTextCharacters { get; init; } = 1 << 20;

    /// <summary>Gets the most dash entries of one pen (default 1,024).</summary>
    public int MaxDashEntries { get; init; } = 1024;

    /// <summary>
    /// Gets the budget of work units over the whole file: one per record, one per path point, one per region rectangle, one per 64 bitmap
    /// pixels, one per text character (default 2^31). It stops files that stay inside every other limit and still ask for too much.
    /// </summary>
    public long MaxWork { get; init; } = 1L << 31;
}

/// <summary>Options for interpreting a metafile.</summary>
public sealed class MetafileReadOptions
{
    /// <summary>Gets the defaults.</summary>
    public static MetafileReadOptions Default { get; } = new();

    /// <summary>Gets the limits (default <see cref="MetafileLimits.Default"/>).</summary>
    public MetafileLimits Limits { get; init; } = MetafileLimits.Default;

    /// <summary>
    /// Gets a function that measures the advance width, in logical units, of a string in a font. It is needed only to move the current position
    /// after text drawn with the update-current-position alignment and no explicit advances. When <see langword="null"/>, half the font height
    /// per character is used. The raster renderer supplies a measurer based on the fonts it draws with.
    /// </summary>
    public Func<FontSpec, string, double>? MeasureText { get; init; }

    /// <summary>
    /// Gets a value indicating whether a Windows metafile that carries an enhanced metafile in its comment records is interpreted from that
    /// enhanced metafile (the default), which is how the applications that write such files intend them to be read. When <see langword="false"/>,
    /// or when the embedded file is damaged, the WMF records themselves are drawn.
    /// </summary>
    public bool PreferEmbeddedEmf { get; init; } = true;
}
