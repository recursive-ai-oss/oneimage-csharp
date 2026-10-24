namespace OneImage;

/// <summary>
/// The set of image formats and the default limits used by load and save calls. <see cref="Default"/> has every
/// built-in codec; build your own with <c>new ImageConfiguration()</c> and <see cref="Add"/> to keep unused codecs
/// out of a trimmed or AOT-compiled app (nothing is discovered by reflection).
/// </summary>
public sealed class ImageConfiguration
{
    private readonly object _gate = new();
    private IImageFormat[] _formats = [];
    private int _headerLength;

    /// <summary>Initializes an empty configuration with <see cref="ImageLimits.Default"/>.</summary>
    public ImageConfiguration()
    {
    }

    /// <summary>
    /// Gets the shared configuration with all built-in codecs. Its <see cref="Limits"/> are the global default limits
    /// and can be changed once at startup.
    /// </summary>
    public static ImageConfiguration Default { get; } = CreateDefault();

    /// <summary>Gets or sets the limits applied when a call does not pass its own.</summary>
    public ImageLimits Limits { get; set; } = ImageLimits.Default;

    /// <summary>Gets the registered formats, in registration order.</summary>
    public IReadOnlyList<IImageFormat> Formats => Volatile.Read(ref _formats);

    /// <summary>Gets how many leading bytes format detection reads.</summary>
    internal int HeaderLength => Volatile.Read(ref _headerLength);

    /// <summary>Registers a format. When two formats match the same bytes, the earlier registration wins.</summary>
    /// <param name="format">The format.</param>
    /// <returns>This configuration.</returns>
    public ImageConfiguration Add(IImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        lock (_gate)
        {
            IImageFormat[] next = [.. _formats, format];
            Volatile.Write(ref _headerLength, Math.Max(_headerLength, format.HeaderLength));
            Volatile.Write(ref _formats, next);
        }

        return this;
    }

    /// <summary>Finds the format whose signature matches.</summary>
    /// <param name="header">Leading bytes of the data.</param>
    /// <returns>The format, or <see langword="null"/> if none matches.</returns>
    public IImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        foreach (IImageFormat f in Formats)
        {
            if (f.Matches(header))
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>Finds a format by name (case-insensitive).</summary>
    /// <param name="name">For example <c>png</c>.</param>
    /// <returns>The format, or <see langword="null"/>.</returns>
    public IImageFormat? FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (IImageFormat f in Formats)
        {
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>Finds a format by file extension (case-insensitive, with or without the leading dot) or by a path ending in it.</summary>
    /// <param name="pathOrExtension">A path or an extension.</param>
    /// <returns>The format, or <see langword="null"/>.</returns>
    public IImageFormat? FindByExtension(string pathOrExtension)
    {
        ArgumentNullException.ThrowIfNull(pathOrExtension);
        string ext = pathOrExtension.Contains('.', StringComparison.Ordinal) ? Path.GetExtension(pathOrExtension) : "." + pathOrExtension;
        foreach (IImageFormat f in Formats)
        {
            foreach (string e in f.Extensions)
            {
                if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
        }

        return null;
    }

    private static ImageConfiguration CreateDefault()
    {
        var config = new ImageConfiguration();
        DefaultCodecs.Register(config);
        return config;
    }
}

/// <summary>The built-in codec list. Each codec adds one line here, so apps that avoid <see cref="ImageConfiguration.Default"/> never reference them.</summary>
internal static class DefaultCodecs
{
    public static void Register(ImageConfiguration configuration)
    {
        configuration.Add(TiffFormat.Instance);
        configuration.Add(JpegFormat.Instance);
        Codecs.Jpeg.JpegTiffBridge.Connect();
        configuration.Add(PngFormat.Instance);
        configuration.Add(BmpFormat.Instance);
        configuration.Add(IcoFormat.Instance);
        configuration.Add(WebPFormat.Instance);
        configuration.Add(GifFormat.Instance);
    }
}

/// <summary>Process-wide scheduling and caching settings.</summary>
public static class ImageEngine
{
    private static int s_maxDegree = Environment.ProcessorCount;
    private static int s_callDegree = Math.Min(4, Environment.ProcessorCount);
    private static long s_regionCacheBytes = 64L * 1024 * 1024;

    /// <summary>Gets or sets the most threads any single call may use (default: processor count).</summary>
    public static int MaxDegree
    {
        get => Volatile.Read(ref s_maxDegree);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref s_maxDegree, value);
        }
    }

    /// <summary>
    /// Gets or sets the degree of parallelism a call uses when it does not pass its own (default: min(4, processor count),
    /// so a busy server is not oversubscribed by one request).
    /// </summary>
    public static int DefaultCallDegree
    {
        get => Volatile.Read(ref s_callDegree);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref s_callDegree, value);
        }
    }

    /// <summary>Gets or sets the byte budget of the global cache of computed tiles (default 256 MiB). Canvas tiles are not counted.</summary>
    public static long CacheBudgetBytes
    {
        get => TileCache.Global.Budget;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            TileCache.Global.Budget = value;
        }
    }

    /// <summary>
    /// Gets or sets the most memory one image frame keeps to make repeated region reads cheap (default 64 MiB; 0 turns it off).
    /// A frame decoded by a streaming sequential decoder (a JPEG or PNG whose pixels take 16 MiB or more in their own format)
    /// normally holds only a few rows, so reading a region it has already moved past would decode the image again. From the second
    /// region read on, <see cref="ImageFrame.CopyPixelsTo{T}"/> keeps the rows it has read, in the pixel format asked for, up to this
    /// many bytes per frame (least recently used format first). A frame whose plane would not fit keeps nothing and behaves as
    /// before. <see cref="Image.ReleaseDecodedPixels"/> gives the memory back.
    /// </summary>
    public static long RegionCacheBytes
    {
        get => Volatile.Read(ref s_regionCacheBytes);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref s_regionCacheBytes, value);
        }
    }

    /// <summary>Resolves a per-call request (0 or less means default) to an effective degree, capped by <see cref="MaxDegree"/>.</summary>
    internal static int ResolveDegree(int requested)
    {
        int d = requested > 0 ? requested : DefaultCallDegree;
        return Math.Clamp(d, 1, MaxDegree);
    }
}
