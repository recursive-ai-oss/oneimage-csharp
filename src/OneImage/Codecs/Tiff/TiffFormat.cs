namespace OneImage;

/// <summary>Compression schemes the TIFF encoder can write.</summary>
public enum TiffCompression
{
    /// <summary>No compression.</summary>
    None = 0,

    /// <summary>LZW as in TIFF 6.0 (most significant bit first, early change).</summary>
    Lzw = 1,

    /// <summary>zlib/Deflate (Adobe-style tag 8).</summary>
    Deflate = 2,

    /// <summary>PackBits run-length coding.</summary>
    PackBits = 3,
}

/// <summary>Settings for writing TIFF files.</summary>
public sealed class TiffEncoderOptions : ImageEncoderOptions
{
    /// <summary>Gets or sets the compression. The default is <see cref="TiffCompression.Lzw"/>.</summary>
    public TiffCompression Compression { get; set; } = TiffCompression.Lzw;

    /// <summary>
    /// Gets or sets whether a predictor is applied before LZW or Deflate: the horizontal predictor for 8- and 16-bit samples
    /// and the floating point predictor for 32-bit float. It is ignored for other compressions. The default is <see langword="true"/>.
    /// </summary>
    public bool UsePredictor { get; set; } = true;

    private int _level = 6;

    /// <summary>Gets or sets the Deflate level, 0 (stored) to 9 (smallest). The default is 6.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..9.</exception>
    public int DeflateLevel
    {
        get => _level;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 9);
            _level = value;
        }
    }

    /// <summary>
    /// Gets or sets the rows stored per strip. 0 (the default) picks strips of about 512 KiB of raw samples, which keeps
    /// region reads cheap and compression effective.
    /// </summary>
    public int RowsPerStrip { get; set; }

    /// <summary>
    /// Gets or sets whether to write BigTIFF (64-bit offsets) instead of classic TIFF. Classic TIFF cannot hold more than 4 GiB;
    /// writing more throws <see cref="ImageLimitException"/>. The default is <see langword="false"/>.
    /// </summary>
    public bool BigTiff { get; set; }
}

/// <summary>
/// The TIFF format (classic and BigTIFF, both byte orders).
/// <para>
/// <b>Read:</b> strips and tiles; compression none, LZW (new and old style), Deflate (tags 8 and 32946), PackBits, CCITT RLE, Group 3
/// (1D/2D) and Group 4; JPEG (7 and 6) through an internal seam that is not connected to a JPEG decoder yet (those files fail with
/// <see cref="ImageFormatException"/>). Horizontal predictor (8/16 bit) and floating point predictor. 1 to 16-bit integer and 32-bit
/// float samples; gray (both polarities), RGB, palette (expanded to RGB8) and CMYK (ink set 1, delivered unconverted); contiguous and
/// planar; the first extra sample is alpha (associated or unassociated). Samples narrower than the output type are scaled to the full
/// range with rounding; min-is-white gray is inverted so zero is black. Not supported: signed, 32-bit integer and 16/64-bit float
/// samples, YCbCr without JPEG, CIELab, transparency masks, differing bit depths per sample.
/// </para>
/// <para>
/// <b>Pages:</b> every directory is a frame with its own size and pixel format; a page that cannot be decoded makes the
/// whole file fail at load, because frame indexes must stay stable.
/// </para>
/// <para>
/// <b>Metadata:</b> ICC, XMP and IPTC are carried as raw blobs; EXIF is exposed as a small self-contained TIFF structure holding the
/// descriptive tags (make, model, software, date, artist, copyright, description) and the EXIF and GPS directories. Orientation is
/// reported, not applied.
/// </para>
/// <para>
/// <b>Region reads:</b> the decoder is random access: a read of a region decompresses only the strips or tiles it intersects, and keeps
/// recently decoded blocks in a small cache so neighboring requests do not decode them again. A file stored as one huge strip must
/// be decoded from its start for any request; it is cached whole (up to 1 GiB of raw samples per strip).
/// </para>
/// <para>
/// <b>Write:</b> see <see cref="TiffEncoderOptions"/>. Formats written: 8/16-bit and 32-bit float gray, RGB, with alpha (straight),
/// and 8/16-bit CMYK; every frame becomes one page.
/// </para>
/// </summary>
public sealed class TiffFormat : IImageFormat
{
    private readonly Codecs.Tiff.TiffDecoder _decoder;
    private readonly Codecs.Tiff.TiffEncoder _encoder = new();

    private TiffFormat() => _decoder = new Codecs.Tiff.TiffDecoder(this);

    /// <summary>Gets the shared instance.</summary>
    public static TiffFormat Instance { get; } = new();

    /// <inheritdoc/>
    public string Name => "tiff";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".tif", ".tiff"];

    /// <inheritdoc/>
    public string MimeType => "image/tiff";

    /// <inheritdoc/>
    public int HeaderLength => 4;

    /// <inheritdoc/>
    public IImageDecoder? Decoder => _decoder;

    /// <inheritdoc/>
    public IImageEncoder? Encoder => _encoder;

    /// <inheritdoc/>
    public bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        bool le = header[0] == 'I' && header[1] == 'I';
        bool be = header[0] == 'M' && header[1] == 'M';
        if (le)
        {
            return (header[2] is 42 or 43) && header[3] == 0;
        }

        return be && header[2] == 0 && header[3] is 42 or 43;
    }
}
