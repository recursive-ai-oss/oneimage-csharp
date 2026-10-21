namespace OneImage;

/// <summary>Registration record of one image format: how to recognize it and its decoder and encoder.</summary>
public interface IImageFormat
{
    /// <summary>Gets the lower-case format name, for example <c>png</c>.</summary>
    string Name { get; }

    /// <summary>Gets file extensions with a leading dot, lower case, for example <c>.png</c>.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Gets the MIME type.</summary>
    string MimeType { get; }

    /// <summary>Gets how many leading bytes <see cref="Matches"/> needs at most.</summary>
    int HeaderLength { get; }

    /// <summary>Returns whether the leading bytes look like this format. Must not throw on short or garbage input.</summary>
    /// <param name="header">Up to <see cref="HeaderLength"/> leading bytes (fewer if the data is shorter).</param>
    /// <returns><see langword="true"/> when the signature matches.</returns>
    bool Matches(ReadOnlySpan<byte> header);

    /// <summary>Gets the decoder, or <see langword="null"/> when the format is write-only.</summary>
    IImageDecoder? Decoder { get; }

    /// <summary>Gets the encoder, or <see langword="null"/> when the format is read-only.</summary>
    IImageEncoder? Encoder { get; }
}

/// <summary>What a decoder can do beyond decoding a whole frame top to bottom.</summary>
[Flags]
public enum DecoderCapabilities
{
    /// <summary>Whole frames only, rows delivered top to bottom.</summary>
    None = 0,

    /// <summary>
    /// <see cref="DecodeRequest.Region"/> is honored (crop push-down): the decoder delivers only that region and may skip the rest.
    /// </summary>
    Region = 1,

    /// <summary>
    /// <see cref="DecodeRequest.ScaleDenominator"/> values 2, 4 and 8 are honored (shrink-on-load, for example JPEG DCT scaling).
    /// The output size is ceil(size / denominator).
    /// </summary>
    Scale = 2,

    /// <summary>
    /// Small regions are cheap to decode (tiled or strip-addressable data), so the core may pull one 128x128 tile at a time
    /// instead of decoding the whole frame once. Implies <see cref="Region"/>.
    /// </summary>
    RandomAccess = 4 | Region,
}

/// <summary>What the core asks a decoder to produce for one frame.</summary>
/// <param name="Region">The wanted rectangle in output coordinates (after scaling). Honored only with <see cref="DecoderCapabilities.Region"/>; the core passes the full frame otherwise.</param>
/// <param name="ScaleDenominator">1, 2, 4 or 8. Anything but 1 is only passed with <see cref="DecoderCapabilities.Scale"/>.</param>
public readonly record struct DecodeRequest(ImageRect Region, int ScaleDenominator = 1);

/// <summary>Receives decoded rows. The core builds one over a line buffer that feeds 128x128 tiles.</summary>
public interface IRowSink
{
    /// <summary>Gets the width in pixels of every row to deliver (the requested region's width).</summary>
    int Width { get; }

    /// <summary>Gets the number of rows to deliver (the requested region's height).</summary>
    int Height { get; }

    /// <summary>Gets the pixel format rows must be delivered in (the frame's <see cref="FrameInfo.Format"/>).</summary>
    PixelFormat Format { get; }

    /// <summary>Delivers rows. Calls must cover the rows 0..Height-1 in order without gaps.</summary>
    /// <param name="y">Index of the first row in <paramref name="rows"/>; must equal the number of rows delivered so far.</param>
    /// <param name="rows">Tightly packed rows of <c>Width * BytesPerPixel</c> bytes each.</param>
    /// <param name="rowCount">Number of rows in <paramref name="rows"/>.</param>
    /// <exception cref="ImageOperationException">Rows arrive out of order or overrun the frame.</exception>
    void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount);
}

/// <summary>
/// Decodes one format. Implementations are stateless and thread-safe; all per-image state lives in the
/// <see cref="ImageInfo"/> they return. Decoders read untrusted bytes: bounds-check everything, call
/// <see cref="ImageLimits.CheckFrame"/> before allocating, turn truncation and corruption into
/// <see cref="ImageFormatException"/>, and let I/O exceptions pass.
/// </summary>
public interface IImageDecoder
{
    /// <summary>Gets what this decoder supports beyond whole-frame sequential decoding.</summary>
    DecoderCapabilities Capabilities { get; }

    /// <summary>Parses headers only and describes the image. Must enforce <paramref name="limits"/> before any large allocation.</summary>
    /// <param name="source">A seekable stream whose position 0 is the first byte of the image.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <returns>The description, with <see cref="ImageInfo.Format"/> set to the registered format.</returns>
    ImageInfo ReadInfo(Stream source, ImageLimits limits);

    /// <summary>Decodes one frame (or the requested part of it), pushing rows into <paramref name="sink"/> top to bottom.</summary>
    /// <param name="source">A seekable stream whose position 0 is the first byte of the image; its position on entry is unspecified.</param>
    /// <param name="info">The value <see cref="ReadInfo"/> returned.</param>
    /// <param name="frameIndex">Zero-based frame index.</param>
    /// <param name="request">Region and scale hint; see <see cref="DecoderCapabilities"/>.</param>
    /// <param name="sink">Where rows go; its size and format already reflect <paramref name="request"/>.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="cancellationToken">Observe at least once per row band.</param>
    void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken);
}

/// <summary>Base class for codec-specific encoder settings (quality, compression level, ...).</summary>
public abstract class ImageEncoderOptions
{
    /// <summary>Initializes the options.</summary>
    protected ImageEncoderOptions()
    {
    }
}

/// <summary>
/// Pulls rows of one frame for an encoder, in the pixel format the encoder asked for. Not thread-safe: call it from the encoder's thread.
/// Reading top to bottom is the cheap order and, for frames from sequential decoders, keeps memory independent of the image height.
/// </summary>
public sealed class ImageFrameReader
{
    private readonly Node _node;
    private readonly RenderContext _context;
    private byte[]? _band;
    private int _bandRow = -1;

    internal ImageFrameReader(Node node, PixelFormat format, TimeSpan delay, RenderContext context)
    {
        _node = node;
        _context = context;
        Format = format;
        Delay = delay;
    }

    /// <summary>Gets the frame width.</summary>
    public int Width => _node.Width;

    /// <summary>Gets the frame height.</summary>
    public int Height => _node.Height;

    /// <summary>Gets the pixel format rows are delivered in (one of the encoder's supported formats).</summary>
    public PixelFormat Format { get; }

    /// <summary>Gets the display time of the frame.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Computes and copies rows. Tiles are computed in parallel up to the call's degree; results do not depend on it.</summary>
    /// <param name="y">First row.</param>
    /// <param name="count">Number of rows.</param>
    /// <param name="destination">Receives <paramref name="count"/> tightly packed rows of <c>Width * Format.BytesPerPixel</c> bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rows lie outside the frame.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public void ReadRows(int y, int count, Span<byte> destination)
    {
        if (!_node.Streaming || count <= 0)
        {
            ReadRegion(new ImageRect(0, y, Width, count), destination, 0);
            return;
        }

        // Streaming frames are rendered one whole tile row at a time into a band, so asking for a few rows at a time
        // does not re-render the tiles of the same tile row again for every call.
        int rowBytes = checked(Width * Format.BytesPerPixel);
        if (y < 0 || count > Height - y)
        {
            throw new ArgumentOutOfRangeException(nameof(y), "The rows lie outside the frame.");
        }

        if ((long)count * rowBytes > destination.Length)
        {
            throw new ArgumentException("Destination is too small for the rows.", nameof(destination));
        }

        int done = 0;
        while (done < count)
        {
            int bandRow = (y + done) >> Tile.Shift;
            int bandTop = bandRow << Tile.Shift;
            if (_bandRow != bandRow)
            {
                _band ??= System.Buffers.ArrayPool<byte>.Shared.Rent(checked(rowBytes * Tile.Size));
                _bandRow = -1;
                Render.ReadRegion(_node, new ImageRect(0, bandTop, Width, Math.Min(Tile.Size, Height - bandTop)), _band, rowBytes, Format, _context);
                _bandRow = bandRow;
            }

            int n = Math.Min(count - done, Math.Min(Tile.Size, Height - bandTop) - (y + done - bandTop));
            _band.AsSpan((y + done - bandTop) * rowBytes, n * rowBytes).CopyTo(destination.Slice(done * rowBytes));
            done += n;
        }
    }

    internal void ReleaseBuffers()
    {
        byte[]? b = _band;
        _band = null;
        _bandRow = -1;
        if (b != null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// <summary>Gets the parallel degree the caller allows (at least 1); encoders may use it for their own parallel stages.</summary>
    internal int Degree => Math.Max(1, _context.Degree);

    /// <summary>Computes and copies a rectangle.</summary>
    /// <param name="region">The rectangle, inside the frame.</param>
    /// <param name="destination">Receives the pixels.</param>
    /// <param name="stride">Bytes between row starts in <paramref name="destination"/>; 0 means tightly packed.</param>
    /// <exception cref="ArgumentOutOfRangeException">The region lies outside the frame.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public void ReadRegion(ImageRect region, Span<byte> destination, int stride) =>
        Render.ReadRegion(_node, region, destination, stride, Format, _context);

    /// <summary>Creates the encoder-facing wrapper for a frame; the format must have been negotiated already.</summary>
    internal static ImageFrameReader Create(ImageFrame frame, PixelFormat format, RenderContext context) =>
        new(frame.Node, format, frame.Delay, context);
}

/// <summary>Everything an encoder needs to write one image.</summary>
public sealed class ImageEncodeRequest
{
    internal ImageEncodeRequest(IReadOnlyList<ImageFrameReader> frames, int loopCount, ImageMetadata metadata, ImageEncoderOptions? options, ImageLimits limits)
    {
        Frames = frames;
        LoopCount = loopCount;
        Metadata = metadata;
        Options = options;
        Limits = limits;
    }

    /// <summary>Gets the frames to write (exactly one unless the encoder reports <see cref="IImageEncoder.SupportsMultipleFrames"/>).</summary>
    public IReadOnlyList<ImageFrameReader> Frames { get; }

    /// <summary>Gets the animation loop count; 0 means forever.</summary>
    public int LoopCount { get; }

    /// <summary>Gets the metadata to embed. The core has not edited it; encoders correct dimension and orientation tags themselves.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>Gets the codec-specific options passed by the caller, or <see langword="null"/>.</summary>
    public ImageEncoderOptions? Options { get; }

    /// <summary>Gets the limits in force.</summary>
    public ImageLimits Limits { get; }
}

/// <summary>
/// Encodes one format. Implementations are stateless and thread-safe. Output must be byte-identical for identical input
/// on a given platform, and must not depend on the degree of parallelism.
/// </summary>
public interface IImageEncoder
{
    /// <summary>
    /// Gets the pixel formats the encoder writes natively, best first. A frame in another format is converted to the
    /// first entry with the same color model (preferring the same sample type), else to the first entry.
    /// </summary>
    IReadOnlyList<PixelFormat> SupportedFormats { get; }

    /// <summary>Gets whether more than one frame can be written; when false only the first frame is passed.</summary>
    bool SupportsMultipleFrames { get; }

    /// <summary>Writes the image.</summary>
    /// <param name="request">The frames and settings.</param>
    /// <param name="destination">The sink; write sequentially, no seeking is guaranteed.</param>
    /// <param name="cancellationToken">Observe at least once per row band.</param>
    void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken);
}
